namespace MesServer.Models;

public record EquipmentSnapshot
{
    public string EquipmentId { get; init; } = "";
    public string EquipmentStatus { get; init; } = "UNKNOWN";
    public string? RecipeId { get; init; }
    public int? CurrentUnitCount { get; init; }
    public int? ExpectedTotalUnits { get; init; }
    public float? CurrentYieldPct { get; init; }
    public string OnlineStatus { get; init; } = EquipmentOnlineStatus.Unknown.ToString();
    public string? LastHeartbeatUtc { get; init; }
    public double? HeartbeatAgeSec { get; init; }
    public int UnacknowledgedAlarmCount { get; init; }
    public string? LatestAlarmLevel { get; init; }
    public string? LatestAlarmCode { get; init; }
    public string? LatestAlarmReason { get; init; }
}

public record DashboardSnapshot
{
    public string TimestampUtc { get; init; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    public IReadOnlyCollection<EquipmentSnapshot> Equipments { get; init; } = Array.Empty<EquipmentSnapshot>();
    public IReadOnlyCollection<ControlRecommendation> Recommendations { get; init; } = Array.Empty<ControlRecommendation>();
    public IReadOnlyCollection<ThresholdProposalRecommendation> ThresholdProposals { get; init; } = Array.Empty<ThresholdProposalRecommendation>();
}

public record CommandResponse(bool Ok, string Message, string? Error = null);

public record EquipmentCommandRequest
{
    public string EquipmentId { get; init; } = "";
    public string? Reason { get; init; }
    public string? BurstId { get; init; }
    public string? RecipeName { get; init; }
}

public record ThresholdProposalCommandRequest
{
    public string EquipmentId { get; init; } = "";
    public string? Reason { get; init; }
}
