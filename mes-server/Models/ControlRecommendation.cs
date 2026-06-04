using System.Text.Json.Serialization;

namespace MesServer.Models;

/// <summary>
/// MES 자동 감지(R23/R25/R26/알람 등)가 만든 "제어 추천/경보".
/// - 모바일은 관제(표시)만: ds/{eq}/recommendation 토픽으로 발행되어 화면에 노출된다.
/// - 실제 제어(처분)는 운영자가 MES 로컬 운영 인터페이스에서만 수행한다 (관제/제어 분리).
/// </summary>
public record ControlRecommendation
{
    [JsonPropertyName("event_type")]        public string EventType        { get; init; } = "CONTROL_RECOMMENDATION";
    [JsonPropertyName("recommendation_id")] public string RecommendationId { get; init; } = Guid.NewGuid().ToString();
    [JsonPropertyName("equipment_id")]      public string EquipmentId      { get; init; } = "";
    [JsonPropertyName("rule")]              public string Rule             { get; init; } = "";   // R23 / R25 / R26 / ALARM
    [JsonPropertyName("severity")]          public string Severity         { get; init; } = "WARNING"; // WARNING / CRITICAL
    [JsonPropertyName("suggested_actions")] public List<string> SuggestedActions { get; init; } = new(); // LOT_ABORT, RECIPE_LOAD, ...
    [JsonPropertyName("reason")]            public string Reason           { get; init; } = "";
    [JsonPropertyName("lot_id")]            public string? LotId           { get; init; }
    [JsonPropertyName("timestamp")]         public string Timestamp        { get; init; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    [JsonPropertyName("status")]            public string Status           { get; init; } = "OPEN"; // OPEN / RESOLVED
}
