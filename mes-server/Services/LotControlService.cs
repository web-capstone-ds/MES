using System.Collections.Concurrent;
using System.Text.Json;
using MesServer.Infrastructure;
using MesServer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Protocol;

namespace MesServer.Services;

public class LotControlService : BackgroundService
{
    private readonly ILogger<LotControlService> _logger;
    private readonly IMqttClientService _mqttClient;
    private readonly RecommendationService _recommendations;
    private readonly ConcurrentDictionary<string, int> _startCount = new();
    private readonly ConcurrentDictionary<string, int> _endCount = new();

    public LotControlService(ILogger<LotControlService> logger, IMqttClientService mqttClient, RecommendationService recommendations)
    {
        _logger = logger;
        _mqttClient = mqttClient;
        _recommendations = recommendations;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SubscribeToLotEventsAsync(stoppingToken);
    }

    private async Task SubscribeToLotEventsAsync(CancellationToken ct)
    {
        await _mqttClient.SubscribeAsync("ds/+/lot", MqttQualityOfServiceLevel.ExactlyOnce, async e =>
        {
            var payload = System.Text.Encoding.UTF8.GetString(e.PayloadSegment);
            var lotEvent = JsonSerializer.Deserialize<LotEvent>(payload);
            if (lotEvent != null)
            {
                await ProcessLotEventAsync(lotEvent);
            }
        }, ct);
    }

    private async Task ProcessLotEventAsync(LotEvent lotEvent)
    {
        switch (lotEvent.EventType)
        {
            case "LOT_START":
                _startCount.AddOrUpdate(lotEvent.EquipmentId, 1, (_, count) => count + 1);
                _logger.LogInformation("[{EqId}] LOT Started: {LotId}", lotEvent.EquipmentId, lotEvent.LotId);
                await CheckImbalanceAsync(lotEvent.EquipmentId);
                break;

            case "LOT_END":
                _endCount.AddOrUpdate(lotEvent.EquipmentId, 1, (_, count) => count + 1);
                if (lotEvent.YieldPct.HasValue)
                {
                    var classification = ClassifyYield(lotEvent.YieldPct.Value);
                    var message = $"[{lotEvent.EquipmentId}] LOT Ended: {lotEvent.LotId} | Yield: {lotEvent.YieldPct:F1}% ({classification})";

                    if (classification == "CRITICAL")
                    {
                        var originalColor = Console.ForegroundColor;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"!!! CRITICAL YIELD: {message}");
                        Console.ForegroundColor = originalColor;
                        _logger.LogCritical("R23 CRITICAL: {Message}", message);

                        // 자동 감지 → 운영자 처분 추천 (수율 위기: 재티칭 또는 LOT 중단 권고)
                        await _recommendations.RaiseAsync(
                            lotEvent.EquipmentId, "R23", "CRITICAL",
                            new[] { "RECIPE_LOAD", "LOT_ABORT" },
                            $"수율 CRITICAL ({lotEvent.YieldPct:F1}%) — 레시피/Teaching 점검 필요",
                            lotEvent.LotId);
                    }
                    else
                    {
                        _logger.LogInformation(message);
                    }
                }
                await CheckImbalanceAsync(lotEvent.EquipmentId);
                break;
        }
    }

    private static string ClassifyYield(float yieldPct) => yieldPct switch
    {
        >= 98 => "EXCELLENT",
        >= 95 => "NORMAL",
        >= 90 => "WARNING",
        >= 80 => "MARGINAL",
        _     => "CRITICAL"
    };

    private async Task CheckImbalanceAsync(string equipmentId)
    {
        var diff = _startCount.GetValueOrDefault(equipmentId)
                 - _endCount.GetValueOrDefault(equipmentId);
        if (diff >= 5)
        {
            _logger.LogCritical("R25 CRITICAL: {EqId} LOT Start/End imbalance detected. Diff: {Diff}",
                                 equipmentId, diff);

            // 자동 감지 → 운영자 처분 추천 (LOT_END 누락 의심: 중단/상태조회 권고)
            await _recommendations.RaiseAsync(
                equipmentId, "R25", "CRITICAL",
                new[] { "LOT_ABORT", "STATUS_QUERY" },
                $"LOT Start/End 불균형 (diff={diff}) — LOT_END 누락 의심",
                lotId: null);
        }
    }

    public async Task SendCommandAsync(
        string equipmentId,
        string command,
        string? reason = null,
        string? targetLotId = null,
        string? targetBurstId = null,
        Dictionary<string, object?>? payload = null,
        CancellationToken ct = default)
    {
        var cmd = new ControlCommand
        {
            EquipmentId = equipmentId,
            Command = command,
            Reason = reason,
            TargetLotId = targetLotId,
            TargetBurstId = targetBurstId,
            Payload = payload
        };

        var topic = $"ds/{equipmentId}/control";
        _logger.LogInformation("Issuing command {Command} to {EqId}", command, equipmentId);
        
        await _mqttClient.PublishAsync(topic, cmd, MqttQualityOfServiceLevel.ExactlyOnce, false, ct);
    }

    public Task EmergencyStopAsync(string id, string reason, CancellationToken ct = default) 
        => SendCommandAsync(id, "EMERGENCY_STOP", reason, ct: ct);

    public Task LotAbortAsync(string id, string lotId, string reason, CancellationToken ct = default) 
        => SendCommandAsync(id, "LOT_ABORT", reason, targetLotId: lotId, ct: ct);

    public Task RecipeLoadAsync(string id, string recipeName, CancellationToken ct = default) 
        => SendCommandAsync(id, "RECIPE_LOAD", $"Load {recipeName}", ct: ct);

    public Task AlarmClearAsync(string id, CancellationToken ct = default) 
        => SendCommandAsync(id, "ALARM_CLEAR", ct: ct);

    public Task StatusQueryAsync(string id, CancellationToken ct = default) 
        => SendCommandAsync(id, "STATUS_QUERY", ct: ct);

    public Task AlarmAckAsync(string id, string? burstId = null, CancellationToken ct = default) 
        => SendCommandAsync(id, "ALARM_ACK", targetBurstId: burstId, ct: ct);

    public Task ApproveThresholdAsync(string equipmentId, string proposalId, string approvedBy, CancellationToken ct = default)
        => SendCommandAsync(
            equipmentId,
            "APPROVE_THRESHOLD",
            payload: new Dictionary<string, object?>
            {
                ["proposal_id"] = proposalId,
                ["approved_by"] = approvedBy
            },
            ct: ct);

    public Task RejectThresholdAsync(string equipmentId, string proposalId, string rejectedBy, string? reason, CancellationToken ct = default)
        => SendCommandAsync(
            equipmentId,
            "REJECT_THRESHOLD",
            reason: reason,
            payload: new Dictionary<string, object?>
            {
                ["proposal_id"] = proposalId,
                ["rejected_by"] = rejectedBy,
                ["reason"] = reason ?? ""
            },
            ct: ct);
}
