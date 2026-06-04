using System.Collections.Concurrent;
using MesServer.Infrastructure;
using MesServer.Models;
using Microsoft.Extensions.Logging;
using MQTTnet.Protocol;

namespace MesServer.Services;

/// <summary>
/// 제어 추천(경보)을 보관하고, 모바일 관제용으로 MQTT(ds/{eq}/recommendation)에 발행한다.
/// 행동(제어)은 하지 않는다 — 운영자가 MES 로컬 운영 인터페이스에서만 처분한다.
/// 장비당 최신 OPEN 추천 1건을 유지한다(retained 메시지와 동일 의미).
/// </summary>
public class RecommendationService
{
    private const string RecommendationTopicSuffix = "recommendation";

    private readonly IMqttClientService _mqtt;
    private readonly ILogger<RecommendationService> _logger;
    private readonly ConcurrentDictionary<string, ControlRecommendation> _open = new();

    public RecommendationService(IMqttClientService mqtt, ILogger<RecommendationService> logger)
    {
        _mqtt = mqtt;
        _logger = logger;
    }

    public IReadOnlyCollection<ControlRecommendation> GetAll() => _open.Values.ToArray();

    public ControlRecommendation? Get(string equipmentId)
        => _open.TryGetValue(equipmentId, out var r) ? r : null;

    /// <summary>자동 감지 결과를 추천으로 등록하고 모바일 관제용으로 발행한다.</summary>
    public async Task RaiseAsync(
        string equipmentId, string rule, string severity,
        IEnumerable<string> suggestedActions, string reason, string? lotId = null,
        CancellationToken ct = default)
    {
        var rec = new ControlRecommendation
        {
            EquipmentId = equipmentId,
            Rule = rule,
            Severity = severity,
            SuggestedActions = suggestedActions.ToList(),
            Reason = reason,
            LotId = lotId,
            Status = "OPEN",
        };
        _open[equipmentId] = rec;
        _logger.LogWarning("[RECO] {Eq} {Rule}/{Sev} → [{Actions}] ({Reason})",
            equipmentId, rule, severity, string.Join(",", rec.SuggestedActions), reason);

        await PublishAsync(equipmentId, rec, ct);
    }

    /// <summary>운영자가 명령을 발동했거나 상황이 해소되면 추천을 종료(clear)한다.</summary>
    public async Task ResolveAsync(string equipmentId, string resolvedBy, CancellationToken ct = default)
    {
        _open.TryRemove(equipmentId, out _);
        var resolved = new ControlRecommendation
        {
            EquipmentId = equipmentId,
            Rule = "",
            Severity = "INFO",
            Reason = $"resolved by {resolvedBy}",
            Status = "RESOLVED",
        };
        _logger.LogInformation("[RECO] {Eq} resolved by {By}", equipmentId, resolvedBy);
        await PublishAsync(equipmentId, resolved, ct);
    }

    private async Task PublishAsync(string equipmentId, ControlRecommendation rec, CancellationToken ct)
    {
        var topic = $"ds/{equipmentId}/{RecommendationTopicSuffix}";
        try
        {
            // QoS1 + retained: 늦게 접속한 관제 단말도 현재 추천 상태를 즉시 받도록 한다.
            await _mqtt.PublishAsync(topic, rec, MqttQualityOfServiceLevel.AtLeastOnce, true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recommendation publish 실패: {Topic}", topic);
        }
    }

    /// <summary>알람 hw_error_code → 운영자에게 권할 제어 명령 매핑.</summary>
    public static IReadOnlyList<string> SuggestActionsForAlarm(string hwErrorCode) => hwErrorCode switch
    {
        // Teaching/비전 품질 문제 → 재티칭 또는 LOT 중단
        "VISION_SCORE_ERR" or "SIDE_VISION_FAIL" => new[] { "RECIPE_LOAD", "LOT_ABORT" },
        // 설비 결함 → 알람 해제 후 복구, 필요 시 비상정지
        "CAM_TIMEOUT_ERR" or "WRITE_FAIL"        => new[] { "ALARM_CLEAR", "EMERGENCY_STOP" },
        "EAP_DISCONNECTED"                        => new[] { "STATUS_QUERY" },
        "RECIPE_CHANGED_NOTICE"                   => new[] { "STATUS_QUERY", "ALARM_ACK" },
        _                                          => new[] { "ALARM_CLEAR" },
    };
}
