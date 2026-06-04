using MesServer.Models;
using MesServer.Services;

namespace MesServer.Api;

public static class DashboardEndpoints
{
    public static WebApplication MapMesDashboardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/dashboard", (
            EquipmentMonitorService equipment,
            RecommendationService recommendations,
            ThresholdProposalService thresholdProposals) =>
        {
            return Results.Ok(new DashboardSnapshot
            {
                Equipments = equipment.GetSnapshot(),
                Recommendations = recommendations.GetAll(),
                ThresholdProposals = thresholdProposals.GetAll()
            });
        });

        app.MapPost("/api/commands/status-query", async (
            EquipmentCommandRequest request,
            LotControlService lot,
            CancellationToken ct) =>
        {
            var validation = ValidateEquipment(request.EquipmentId);
            if (validation is not null) return validation;

            await lot.StatusQueryAsync(request.EquipmentId, ct);
            return Ok($"STATUS_QUERY 발행: {request.EquipmentId}");
        });

        app.MapPost("/api/commands/alarm-ack", async (
            EquipmentCommandRequest request,
            LotControlService lot,
            RecommendationService recommendations,
            CancellationToken ct) =>
        {
            var validation = ValidateEquipment(request.EquipmentId);
            if (validation is not null) return validation;

            await lot.AlarmAckAsync(request.EquipmentId, request.BurstId, ct);
            await recommendations.ResolveAsync(request.EquipmentId, "ALARM_ACK", ct);
            return Ok($"ALARM_ACK 발행: {request.EquipmentId}");
        });

        app.MapPost("/api/commands/alarm-clear", async (
            EquipmentCommandRequest request,
            LotControlService lot,
            RecommendationService recommendations,
            CancellationToken ct) =>
        {
            var validation = ValidateEquipment(request.EquipmentId);
            if (validation is not null) return validation;

            await lot.AlarmClearAsync(request.EquipmentId, ct);
            await recommendations.ResolveAsync(request.EquipmentId, "ALARM_CLEAR", ct);
            return Ok($"ALARM_CLEAR 발행: {request.EquipmentId}");
        });

        app.MapPost("/api/commands/emergency-stop", async (
            EquipmentCommandRequest request,
            LotControlService lot,
            RecommendationService recommendations,
            CancellationToken ct) =>
        {
            var validation = ValidateEquipment(request.EquipmentId);
            if (validation is not null) return validation;

            await lot.EmergencyStopAsync(request.EquipmentId, request.Reason ?? "operator emergency stop", ct);
            await recommendations.ResolveAsync(request.EquipmentId, "EMERGENCY_STOP", ct);
            return Ok($"EMERGENCY_STOP 발행: {request.EquipmentId}");
        });

        app.MapPost("/api/commands/lot-abort", async (
            EquipmentCommandRequest request,
            LotControlService lot,
            RecommendationService recommendations,
            CancellationToken ct) =>
        {
            var validation = ValidateEquipment(request.EquipmentId);
            if (validation is not null) return validation;

            await lot.LotAbortAsync(request.EquipmentId, lotId: "", request.Reason ?? "operator abort", ct);
            await recommendations.ResolveAsync(request.EquipmentId, "LOT_ABORT", ct);
            return Ok($"LOT_ABORT 발행: {request.EquipmentId}");
        });

        app.MapPost("/api/commands/recipe-load", async (
            EquipmentCommandRequest request,
            LotControlService lot,
            RecommendationService recommendations,
            CancellationToken ct) =>
        {
            var validation = ValidateEquipment(request.EquipmentId);
            if (validation is not null) return validation;
            if (string.IsNullOrWhiteSpace(request.RecipeName))
                return BadRequest("recipeName is required.");

            var recipeName = request.RecipeName.Trim();
            await lot.RecipeLoadAsync(request.EquipmentId, recipeName, ct);
            await recommendations.ResolveAsync(request.EquipmentId, "RECIPE_LOAD", ct);
            return Ok($"RECIPE_LOAD({recipeName}) 발행: {request.EquipmentId}");
        });

        app.MapPost("/api/threshold-proposals/{proposalId}/approve", async (
            string proposalId,
            ThresholdProposalCommandRequest request,
            LotControlService lot,
            ThresholdProposalService thresholdProposals,
            CancellationToken ct) =>
        {
            var validation = ValidateEquipment(request.EquipmentId);
            if (validation is not null) return validation;
            if (string.IsNullOrWhiteSpace(proposalId))
                return BadRequest("proposalId is required.");

            await lot.ApproveThresholdAsync(request.EquipmentId, proposalId, CurrentOperator(), ct);
            thresholdProposals.Remove(proposalId);
            return Ok($"APPROVE_THRESHOLD 발행: {request.EquipmentId} {proposalId}");
        });

        app.MapPost("/api/threshold-proposals/{proposalId}/reject", async (
            string proposalId,
            ThresholdProposalCommandRequest request,
            LotControlService lot,
            ThresholdProposalService thresholdProposals,
            CancellationToken ct) =>
        {
            var validation = ValidateEquipment(request.EquipmentId);
            if (validation is not null) return validation;
            if (string.IsNullOrWhiteSpace(proposalId))
                return BadRequest("proposalId is required.");

            await lot.RejectThresholdAsync(request.EquipmentId, proposalId, CurrentOperator(), request.Reason, ct);
            thresholdProposals.Remove(proposalId);
            return Ok($"REJECT_THRESHOLD 발행: {request.EquipmentId} {proposalId}");
        });

        app.MapFallbackToFile("index.html");
        return app;
    }

    private static IResult? ValidateEquipment(string equipmentId)
        => string.IsNullOrWhiteSpace(equipmentId)
            ? BadRequest("equipmentId is required.")
            : null;

    private static IResult Ok(string message)
        => Results.Ok(new CommandResponse(true, message));

    private static IResult BadRequest(string error)
        => Results.BadRequest(new CommandResponse(false, "", error));

    private static string CurrentOperator()
        => string.IsNullOrWhiteSpace(Environment.UserName) ? "operator" : Environment.UserName;
}
