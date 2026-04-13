using System.Text.Json.Serialization;

namespace MesServer.Models;

public record ControlCommand
{
    [JsonPropertyName("message_id")]      public string  MessageId     { get; init; } = Guid.NewGuid().ToString();
    [JsonPropertyName("event_type")]      public string  EventType     { get; init; } = "CONTROL_CMD";
    [JsonPropertyName("timestamp")]       public string  Timestamp     { get; init; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    [JsonPropertyName("command")]         public string  Command       { get; init; } = "";
    [JsonPropertyName("issued_by")]       public string  IssuedBy      { get; init; } = "MES_SERVER";
    [JsonPropertyName("reason")]          public string? Reason        { get; init; }
    [JsonPropertyName("target_lot_id")]   public string? TargetLotId   { get; init; }
    [JsonPropertyName("target_burst_id")] public string? TargetBurstId { get; init; }
}

public record LotEvent
{
    [JsonPropertyName("event_type")]      public string  EventType     { get; init; } = "";
    [JsonPropertyName("equipment_id")]    public string  EquipmentId   { get; init; } = "";
    [JsonPropertyName("lot_id")]          public string  LotId         { get; init; } = "";
    [JsonPropertyName("yield_pct")]       public float?  YieldPct      { get; init; }
    [JsonPropertyName("total_unit_count")] public int?   TotalUnitCount { get; init; }
}
