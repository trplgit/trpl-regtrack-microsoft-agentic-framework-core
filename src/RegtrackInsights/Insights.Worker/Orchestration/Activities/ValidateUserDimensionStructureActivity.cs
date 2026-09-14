using System.Text.Json;
using DurableTask.Core;
using Insights.Domain;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

/// <param name="UsersRowsJson">
/// [ADDED 2026-09-15] The real `dimension_rows.Users` array, as raw JSON - lets this activity
/// tell the gate whether a real qualifying row exists (TimingSampleSize >= 5) without the gate
/// itself parsing domain types from HTML text. Null/empty for every report the gate skips anyway
/// (see the ReportType/RequestedDimensions guard below).
/// </param>
public sealed record ValidateUserDimensionStructureInput(string Html, string ReportType, IReadOnlyList<string>? RequestedDimensions, string? UsersRowsJson = null);
public sealed record ValidateUserDimensionStructureOutput(string Html);

/// <summary>
/// [ADDED 2026-09-12] Deterministic structural gate on the rendered report
/// (UserDimensionStructureGate, Insights.Presentation) - see that class's own doc comment for
/// the real render that ignored the rewritten dimension_selection:Users template entirely.
/// CLAUDE.md Sec.11: a structural invariant, not a data-sanity check - THROWS, never warns,
/// same posture as ValidateFixedHolisticStructureActivity.
///
/// Skips evaluation entirely unless ReportType == DimensionSelectionComposition.ReportType AND
/// RequestedDimensions is exactly ["Users"] - every other report shape (fixed_holistic, or any
/// other single dimension) legitimately has none of this template's markup and must never be
/// evaluated against it, same reasoning ValidateFixedHolisticStructureActivity's own doc
/// comment gives for its ReportType guard.
/// </summary>
public sealed class ValidateUserDimensionStructureActivity : AsyncTaskActivity<ValidateUserDimensionStructureInput, ValidateUserDimensionStructureOutput>
{
    protected override Task<ValidateUserDimensionStructureOutput> ExecuteAsync(TaskContext context, ValidateUserDimensionStructureInput input) => RunAsync(input);

    internal Task<ValidateUserDimensionStructureOutput> RunAsync(ValidateUserDimensionStructureInput input)
    {
        if (input.ReportType != DimensionSelectionComposition.ReportType || input.RequestedDimensions is not ["Users"])
            return Task.FromResult(new ValidateUserDimensionStructureOutput(input.Html));

        var hasQualifyingTimingRow = HasQualifyingTimingRow(input.UsersRowsJson);
        var result = UserDimensionStructureGate.Evaluate(input.Html, hasQualifyingTimingRow);
        if (!result.Approved)
            throw new OrchestrationRefusedException(
                "USER_DIMENSION_STRUCTURE_INVALID",
                "We couldn't generate this report to our accuracy standard. Our team has been notified.",
                result.Violations);

        return Task.FromResult(new ValidateUserDimensionStructureOutput(input.Html));
    }

    /// <summary>
    /// `usersRowsJson` is the bare `Rows` array (already extracted from the full
    /// `DimensionResult&lt;UsersControlTotals, UsersRow&gt;` JSON by the orchestrator - see
    /// InsightsReportOrchestrator.cs's own `dimensionRowsJson` comment), deserialized with the
    /// SAME plain, no-naming-policy `JsonSerializer` FetchDimensionsActivity serialized it with -
    /// PascalCase property names match `UsersRow` exactly, no options needed.
    /// </summary>
    private static bool HasQualifyingTimingRow(string? usersRowsJson)
    {
        if (string.IsNullOrWhiteSpace(usersRowsJson))
            return false;

        var rows = JsonSerializer.Deserialize<List<UsersRow>>(usersRowsJson);
        return rows?.Any(r => r.TimingSampleSize is >= 5) ?? false;
    }
}
