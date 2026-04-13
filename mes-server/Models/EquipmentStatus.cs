using System.Text.Json.Serialization;

namespace MesServer.Models;

public record HeartbeatEvent
{
    [JsonPropertyName("equipment_id")] public string EquipmentId { get; init; } = "";
    [JsonPropertyName("timestamp")]    public string Timestamp { get; init; } = "";
}

public record StatusEvent
{
    [JsonPropertyName("equipment_id")]         public string  EquipmentId         { get; init; } = "";
    [JsonPropertyName("equipment_status")]     public string  Status              { get; init; } = "IDLE";
    [JsonPropertyName("current_recipe")]       public string? CurrentRecipe       { get; init; }
    [JsonPropertyName("current_unit_count")]    public int?    CurrentUnitCount    { get; init; }
    [JsonPropertyName("expected_total_units")]  public int?    ExpectedTotalUnits  { get; init; }
    [JsonPropertyName("current_yield_pct")]     public float?  CurrentYieldPct     { get; init; }
}

public record AlarmEvent
{
    [JsonPropertyName("equipment_id")]                public string  EquipmentId                { get; init; } = "";
    [JsonPropertyName("alarm_level")]                public string  AlarmLevel                { get; init; } = "WARNING";
    [JsonPropertyName("hw_error_code")]               public string  HwErrorCode               { get; init; } = "";
    [JsonPropertyName("reason")]                      public string? Reason                    { get; init; } = "";
    [JsonPropertyName("requires_manual_intervention")] public bool    RequiresManualIntervention { get; init; }
}

public enum EquipmentOnlineStatus
{
    Unknown,
    Online,
    Warning,
    Offline
}
