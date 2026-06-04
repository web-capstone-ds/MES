using System.Collections.Concurrent;
using System.Text.Json;
using MesServer.Infrastructure;
using MesServer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Protocol;

namespace MesServer.Services;

public class EquipmentMonitorService : BackgroundService
{
    private readonly ILogger<EquipmentMonitorService> _logger;
    private readonly IMqttClientService _mqttClient;

    private readonly ConcurrentDictionary<string, DateTime> _lastHeartbeat = new();
    private readonly ConcurrentDictionary<string, StatusEvent> _currentStatus = new();
    private readonly ConcurrentDictionary<string, List<AlarmEvent>> _unacknowledgedAlarms = new();

    // Rule Counters
    private readonly ConcurrentDictionary<string, int> _dailyCamTimeoutCount = new();
    private readonly ConcurrentDictionary<string, int> _dailyAggregateExCount = new();
    private readonly ConcurrentDictionary<string, int> _weeklyDisconnectedCount = new();
    
    private DateTime _nextDailyReset = DateTime.UtcNow.Date.AddDays(1);
    private DateTime _nextWeeklyReset = GetNextMondayUtc();

    private readonly RecommendationService _recommendations;

    // 운영자 콘솔(stdin) 제어가 주 인터페이스이므로, 화면을 5초마다 Console.Clear() 하는
    // 모니터 패널은 기본 비활성화한다(입력 충돌 방지). MES_SHOW_PANEL=true 로 재활성 가능.
    private readonly bool _showPanel =
        string.Equals(Environment.GetEnvironmentVariable("MES_SHOW_PANEL"), "true", StringComparison.OrdinalIgnoreCase);

    public EquipmentMonitorService(ILogger<EquipmentMonitorService> logger, IMqttClientService mqttClient, RecommendationService recommendations)
    {
        _logger = logger;
        _mqttClient = mqttClient;
        _recommendations = recommendations;
    }

    private static DateTime GetNextMondayUtc()
    {
        var now = DateTime.UtcNow;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0) daysUntilMonday = 7;
        return now.Date.AddDays(daysUntilMonday);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe to relevant topics
        await _mqttClient.SubscribeAsync("ds/+/heartbeat", MqttQualityOfServiceLevel.AtLeastOnce, HandleHeartbeat, stoppingToken);
        await _mqttClient.SubscribeAsync("ds/+/status", MqttQualityOfServiceLevel.AtLeastOnce, HandleStatus, stoppingToken);
        await _mqttClient.SubscribeAsync("ds/+/alarm", MqttQualityOfServiceLevel.ExactlyOnce, HandleAlarm, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckResets();
                if (_showPanel) DisplayMonitorPanel();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in EquipmentMonitorService loop.");
            }

            await Task.Delay(5000, stoppingToken);
        }
    }

    private void CheckResets()
    {
        var now = DateTime.UtcNow;
        if (now >= _nextDailyReset)
        {
            _dailyCamTimeoutCount.Clear();
            _dailyAggregateExCount.Clear();
            _nextDailyReset = now.Date.AddDays(1);
            _logger.LogInformation("Daily counters reset at midnight UTC.");
        }

        if (now >= _nextWeeklyReset)
        {
            _weeklyDisconnectedCount.Clear();
            _nextWeeklyReset = GetNextMondayUtc();
            _logger.LogInformation("Weekly counters reset at Monday midnight UTC.");
        }
    }

    private Task HandleHeartbeat(MQTTnet.MqttApplicationMessage message)
    {
        var evt = JsonSerializer.Deserialize<HeartbeatEvent>(System.Text.Encoding.UTF8.GetString(message.PayloadSegment));
        if (evt != null)
        {
            _lastHeartbeat[evt.EquipmentId] = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    private Task HandleStatus(MQTTnet.MqttApplicationMessage message)
    {
        var evt = JsonSerializer.Deserialize<StatusEvent>(System.Text.Encoding.UTF8.GetString(message.PayloadSegment));
        if (evt != null)
        {
            _currentStatus[evt.EquipmentId] = evt;
        }
        return Task.CompletedTask;
    }

    private async Task HandleAlarm(MQTTnet.MqttApplicationMessage message)
    {
        if (message.PayloadSegment.Count == 0)
        {
            return;
        }

        var evt = JsonSerializer.Deserialize<AlarmEvent>(System.Text.Encoding.UTF8.GetString(message.PayloadSegment));
        if (evt == null) return;

        // Special Case: EAP_DISCONNECTED (§부록 A.5)
        if (evt.HwErrorCode == "EAP_DISCONNECTED")
        {
            _logger.LogCritical("§부록 A.5 Will 수신: {EqId} 연결 단절됨. Heartbeat 무시 즉시 STOP 처리.", evt.EquipmentId);
            _lastHeartbeat[evt.EquipmentId] = DateTime.MinValue; // Force Offline
            IncrementRuleCounter(_weeklyDisconnectedCount, evt.EquipmentId, 2, "R34 (EAP_DISCONNECTED/주)");
        }

        // Rule Counters
        if (evt.HwErrorCode == "CAM_TIMEOUT_ERR")
            IncrementRuleCounter(_dailyCamTimeoutCount, evt.EquipmentId, 3, "R26 (CAM_TIMEOUT_ERR/일)");
        
        if (evt.HwErrorCode == "VISION_SCORE_ERR" && (evt.Reason?.Contains("AggregateException") ?? false))
            IncrementRuleCounter(_dailyAggregateExCount, evt.EquipmentId, 5, "R33 (AggregateException/일)");

        // Logging with color
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = evt.AlarmLevel == "CRITICAL" ? ConsoleColor.Red : ConsoleColor.Yellow;
        
        var logMessage = $"[{evt.EquipmentId}] {evt.AlarmLevel} ALARM: {evt.HwErrorCode} - {evt.Reason}";
        Console.WriteLine($"!!! {logMessage}");
        Console.ForegroundColor = originalColor;

        if (evt.AlarmLevel == "CRITICAL") _logger.LogCritical(logMessage);
        else _logger.LogWarning(logMessage);

        if (evt.RequiresManualIntervention)
        {
            var list = _unacknowledgedAlarms.GetOrAdd(evt.EquipmentId, _ => new List<AlarmEvent>());
            lock (list) { list.Add(evt); }
        }

        // 자동 감지 → 운영자 처분 추천: 운영자 개입이 필요한 알람(예: Teaching 미완성 VISION_SCORE_ERR
        // WARNING, requires_manual_intervention=true) 또는 CRITICAL 알람을 추천으로 노출한다.
        if (evt.RequiresManualIntervention || evt.AlarmLevel == "CRITICAL")
        {
            await _recommendations.RaiseAsync(
                evt.EquipmentId, "ALARM", evt.AlarmLevel,
                RecommendationService.SuggestActionsForAlarm(evt.HwErrorCode),
                $"{evt.HwErrorCode} - {evt.Reason}",
                lotId: null);
        }
    }

    public IReadOnlyCollection<EquipmentSnapshot> GetSnapshot()
    {
        var ids = _currentStatus.Keys
            .Concat(_lastHeartbeat.Keys)
            .Concat(_unacknowledgedAlarms.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var now = DateTime.UtcNow;
        var snapshots = new List<EquipmentSnapshot>(ids.Length);

        foreach (var id in ids)
        {
            _currentStatus.TryGetValue(id, out var status);
            _lastHeartbeat.TryGetValue(id, out var lastHeartbeat);
            var onlineStatus = GetOnlineStatus(id);
            var alarmCount = 0;
            AlarmEvent? latestAlarm = null;

            if (_unacknowledgedAlarms.TryGetValue(id, out var alarms))
            {
                lock (alarms)
                {
                    alarmCount = alarms.Count;
                    latestAlarm = alarms.LastOrDefault();
                }
            }

            snapshots.Add(new EquipmentSnapshot
            {
                EquipmentId = id,
                EquipmentStatus = status?.Status ?? "UNKNOWN",
                RecipeId = status?.CurrentRecipe,
                CurrentUnitCount = status?.CurrentUnitCount,
                ExpectedTotalUnits = status?.ExpectedTotalUnits,
                CurrentYieldPct = status?.CurrentYieldPct,
                OnlineStatus = onlineStatus.ToString(),
                LastHeartbeatUtc = lastHeartbeat == default ? null : lastHeartbeat.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                HeartbeatAgeSec = lastHeartbeat == default ? null : Math.Round((now - lastHeartbeat).TotalSeconds, 1),
                UnacknowledgedAlarmCount = alarmCount,
                LatestAlarmLevel = latestAlarm?.AlarmLevel,
                LatestAlarmCode = latestAlarm?.HwErrorCode,
                LatestAlarmReason = latestAlarm?.Reason
            });
        }

        return snapshots;
    }

    private void IncrementRuleCounter(ConcurrentDictionary<string, int> counter, string id, int threshold, string ruleName)
    {
        var current = counter.AddOrUpdate(id, 1, (_, count) => count + 1);
        if (current > threshold)
        {
            _logger.LogCritical("{Rule} 위반: {EqId} 건수={Count} (임계치 {Threshold} 초과)", ruleName, id, current, threshold);
        }
    }

    private void DisplayMonitorPanel()
    {
        Console.Clear();
        Console.WriteLine($"--- MES 장비 모니터링 패널 ({DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC) ---");
        
        // In a real scenario, we'd get the full equipment list from configuration
        foreach (var id in _currentStatus.Keys.OrderBy(k => k))
        {
            var status = _currentStatus[id];
            var online = GetOnlineStatus(id);
            var color = GetStatusColor(status.Status, online);
            
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = color;

            string progressStr = "";
            if (status.CurrentUnitCount.HasValue)
            {
                var expected = status.ExpectedTotalUnits.HasValue ? status.ExpectedTotalUnits.Value.ToString() : "?";
                var pct = status.ExpectedTotalUnits.HasValue ? (status.CurrentUnitCount.Value * 100.0 / status.ExpectedTotalUnits.Value) : 0;
                progressStr = $"{status.CurrentUnitCount.Value:N0} / {expected} ({pct:F1}%)";
            }
            else
            {
                progressStr = status.Status == "IDLE" ? "LOT 완료" : "진행 정보 없음";
            }

            string yieldStr = status.CurrentYieldPct.HasValue ? $" | 수율 {status.CurrentYieldPct:F1}%" : "";
            if (status.CurrentYieldPct.HasValue && status.CurrentYieldPct < 90) yieldStr += " ⚠ WARNING";

            string alarmIndicator = _unacknowledgedAlarms.TryGetValue(id, out var alarms) && alarms.Count > 0 
                ? " ● ALARM" : "";

            Console.WriteLine($"[{id}] {status.Status,-4} | {status.CurrentRecipe,-10} | {progressStr,-20}{yieldStr}{alarmIndicator}");
            Console.ForegroundColor = originalColor;
        }
        Console.WriteLine("------------------------------------------------------------------");
    }

    private EquipmentOnlineStatus GetOnlineStatus(string equipmentId)
    {
        if (!_lastHeartbeat.TryGetValue(equipmentId, out var last))
            return EquipmentOnlineStatus.Unknown;

        return (DateTime.UtcNow - last).TotalSeconds switch
        {
            <= 9  => EquipmentOnlineStatus.Online,   // R01 정상
            <= 30 => EquipmentOnlineStatus.Warning,  // R01 WARNING
            _     => EquipmentOnlineStatus.Offline   // R01 CRITICAL (Heartbeat timeout)
        };
    }

    private static ConsoleColor GetStatusColor(string status, EquipmentOnlineStatus online)
    {
        if (online == EquipmentOnlineStatus.Offline) return ConsoleColor.DarkGray;
        if (online == EquipmentOnlineStatus.Warning) return ConsoleColor.Yellow;

        return status switch
        {
            "RUN"  => ConsoleColor.Green,
            "STOP" => ConsoleColor.Red,
            "IDLE" => ConsoleColor.Cyan,
            _      => ConsoleColor.White
        };
    }
}
