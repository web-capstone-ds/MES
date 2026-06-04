using System.Collections.Concurrent;
using System.Text.Json;
using MesServer.Infrastructure;
using MesServer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Protocol;

namespace MesServer.Services;

/// <summary>
/// Oracle의 ORACLE_ANALYSIS retained 메시지에서 pending threshold proposal을 캐싱한다.
/// 운영자는 MES 콘솔의 treco 명령으로 proposal_id를 확인한 뒤 tapprove/treject를 발행한다.
/// </summary>
public class ThresholdProposalService : BackgroundService
{
    private readonly ILogger<ThresholdProposalService> _logger;
    private readonly IMqttClientService _mqttClient;
    private readonly ConcurrentDictionary<string, ThresholdProposalRecommendation> _pending = new();

    public ThresholdProposalService(ILogger<ThresholdProposalService> logger, IMqttClientService mqttClient)
    {
        _logger = logger;
        _mqttClient = mqttClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _mqttClient.SubscribeAsync("ds/+/oracle", MqttQualityOfServiceLevel.ExactlyOnce, e =>
        {
            ProcessOracleAnalysis(e.Topic, e.PayloadSegment);
            return Task.CompletedTask;
        }, stoppingToken);
    }

    public IReadOnlyCollection<ThresholdProposalRecommendation> GetAll()
        => _pending.Values
            .OrderBy(r => r.EquipmentId, StringComparer.Ordinal)
            .ThenBy(r => r.ProposalId, StringComparer.Ordinal)
            .ToArray();

    public bool Remove(string proposalId)
        => _pending.TryRemove(proposalId, out _);

    private void ProcessOracleAnalysis(string topic, ArraySegment<byte> payloadSegment)
    {
        if (payloadSegment.Count == 0)
            return;

        try
        {
            using var doc = JsonDocument.Parse(payloadSegment);
            var root = doc.RootElement;
            if (!root.TryGetProperty("threshold_proposal", out var proposal)
                || proposal.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var proposalId = GetString(proposal, "proposal_id");
            if (string.IsNullOrWhiteSpace(proposalId))
                return;

            var status = GetString(proposal, "status") ?? "PENDING";
            if (!string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase))
            {
                Remove(proposalId);
                _logger.LogInformation("Threshold proposal processed: {ProposalId} status={Status}", proposalId, status);
                return;
            }

            var equipmentId = GetString(root, "equipment_id") ?? TopicEquipment(topic);
            var record = new ThresholdProposalRecommendation
            {
                EquipmentId = equipmentId,
                ProposalId = proposalId,
                RecipeId = GetString(proposal, "recipe_id"),
                RuleId = GetString(proposal, "rule_id"),
                Metric = GetString(proposal, "metric"),
                CurrentWarning = GetDouble(proposal, "current_warning"),
                CurrentCritical = GetDouble(proposal, "current_critical"),
                ProposedWarning = GetDouble(proposal, "proposed_warning"),
                ProposedCritical = GetDouble(proposal, "proposed_critical"),
                LotBasis = GetInt(proposal, "lot_basis"),
                Basis = GetString(proposal, "basis"),
                Timestamp = GetString(root, "timestamp") ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            };
            _pending[proposalId] = record;
            _logger.LogInformation("Threshold proposal pending: {Eq} {ProposalId}", equipmentId, proposalId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ORACLE_ANALYSIS threshold proposal parse failed: {Topic}", topic);
        }
    }

    private static string TopicEquipment(string topic)
    {
        var parts = topic.Split('/');
        return parts.Length >= 3 ? parts[1] : "";
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        return double.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }
}
