using System.Text.Json;
using DurableTask.Core;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record FetchDimensionsInput(int UserId, int CustomerId);

public sealed record FetchDimensionsOutput(
    IReadOnlyDictionary<string, string> DimensionResults,
    IReadOnlyList<Assertion> Assertions,
    IReadOnlyList<Finding> Findings);

/// <summary>
/// Nodes 3-4: the nine dimension calls, each of which already returns its own Assertions/Findings
/// (already reconciled, already validated - see DimensionResult.Validate, called automatically by
/// SqlDimensionRepository). This is "validating" in API_CONTRACTS.md's stage vocabulary because
/// the reconciliation THROWs happen inside these calls, not as a separate step.
///
/// Each DimensionResult&lt;TControlTotals,TRow&gt; is serialized to a JSON string, not a
/// System.Text.Json.JsonElement as first tried. A generic type closed differently per dimension
/// does not survive Durable Task's own round-trip as `object` (no discriminator for the
/// deserializer to pick the right closed type back up) - JsonElement was meant to fix that, but
/// classic DTFx's default DataConverter wraps Newtonsoft.Json, which cannot reconstruct an
/// STJ-specific JsonElement struct. Confirmed live: a real run against tenant 29 failed at the
/// composing stage with "Operation is not valid due to the current state of the object" - exactly
/// where DimensionResults first crosses an activity boundary. A plain string round-trips through
/// any serializer; ComposeActivity parses it back to a JsonElement locally, after DTFx's own
/// deserialization has already happened, never inside the cross-boundary payload itself.
/// </summary>
public sealed class FetchDimensionsActivity(IDimensionRepository dimensionRepository)
    : AsyncTaskActivity<FetchDimensionsInput, FetchDimensionsOutput>
{
    protected override Task<FetchDimensionsOutput> ExecuteAsync(TaskContext context, FetchDimensionsInput input) => RunAsync(input);

    internal async Task<FetchDimensionsOutput> RunAsync(FetchDimensionsInput input)
    {
        var location = await dimensionRepository.GetLocationAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None);
        var entity = await dimensionRepository.GetEntityAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None);
        var risk = await dimensionRepository.GetRiskAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None);
        var nature = await dimensionRepository.GetNatureAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None);
        var departments = await dimensionRepository.GetDepartmentsAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None);
        var act = await dimensionRepository.GetActAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None);
        var users = await dimensionRepository.GetUsersAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None);
        var @internal = await dimensionRepository.GetInternalAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None);
        var @event = await dimensionRepository.GetEventAsync(input.UserId, input.CustomerId, cancellationToken: CancellationToken.None);

        var dimensionResults = new Dictionary<string, string>
        {
            ["Location"] = JsonSerializer.Serialize(location),
            ["Entity"] = JsonSerializer.Serialize(entity),
            ["Risk"] = JsonSerializer.Serialize(risk),
            ["Nature"] = JsonSerializer.Serialize(nature),
            ["Departments"] = JsonSerializer.Serialize(departments),
            ["Act"] = JsonSerializer.Serialize(act),
            ["Users"] = JsonSerializer.Serialize(users),
            ["Internal"] = JsonSerializer.Serialize(@internal),
            ["Event"] = JsonSerializer.Serialize(@event),
        };

        var assertions = location.Assertions.Concat(entity.Assertions).Concat(risk.Assertions)
            .Concat(nature.Assertions).Concat(departments.Assertions).Concat(act.Assertions)
            .Concat(users.Assertions).Concat(@internal.Assertions).Concat(@event.Assertions).ToList();

        var findings = location.Findings.Concat(entity.Findings).Concat(risk.Findings)
            .Concat(nature.Findings).Concat(departments.Findings).Concat(act.Findings)
            .Concat(users.Findings).Concat(@internal.Findings).Concat(@event.Findings).ToList();

        return new FetchDimensionsOutput(dimensionResults, assertions, findings);
    }
}
