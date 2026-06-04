using System.Text.Json.Serialization;

namespace MesServer.Models;

public record ThresholdProposalRecommendation
{
    [JsonPropertyName("equipment_id")]       public string EquipmentId       { get; init; } = "";
    [JsonPropertyName("proposal_id")]        public string ProposalId        { get; init; } = "";
    [JsonPropertyName("recipe_id")]          public string? RecipeId         { get; init; }
    [JsonPropertyName("rule_id")]            public string? RuleId           { get; init; }
    [JsonPropertyName("metric")]             public string? Metric           { get; init; }
    [JsonPropertyName("current_warning")]    public double? CurrentWarning   { get; init; }
    [JsonPropertyName("current_critical")]   public double? CurrentCritical  { get; init; }
    [JsonPropertyName("proposed_warning")]   public double? ProposedWarning  { get; init; }
    [JsonPropertyName("proposed_critical")]  public double? ProposedCritical { get; init; }
    [JsonPropertyName("lot_basis")]          public int? LotBasis            { get; init; }
    [JsonPropertyName("basis")]              public string? Basis            { get; init; }
    [JsonPropertyName("timestamp")]          public string Timestamp         { get; init; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
