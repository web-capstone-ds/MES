using System.Text.Json.Serialization;

namespace MesServer.Scenarios;

public record ScenarioConfig
{
    [JsonPropertyName("equipments")]
    public List<EquipmentScenarioConfig> Equipments { get; init; } = new();
}

public record EquipmentScenarioConfig
{
    [JsonPropertyName("equipment_id")]
    public string EquipmentId { get; init; } = "";

    [JsonPropertyName("mock_sequence")]
    public List<string> MockSequence { get; init; } = new();
}

public record MockEvent
{
    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = "";
    
    // The rest of the fields vary by event type, so we'll handle the raw JSON for substitution
}
