using System.Text.Json;
using DurableTask.Core;
using Insights.Data;
using Insights.Domain;

namespace Insights.Worker.Orchestration.Activities;

public sealed record FetchDimensionsInput(int UserId, int CustomerId);

public sealed record FetchDimensionsOutput(
    IReadOnlyDictionary<string, JsonElement> DimensionResults,
    IReadOnlyList<Assertion> Assertions,
    IReadOnlyList<Finding> Findings);

/// <summary>
/// Nodes 3-4: the nine dimension calls, each of which already returns its own Assertions/Findings
/// (already reconciled, already validated - see DimensionResult.Validate, called automatically by
/// SqlDimensionRepository). This is "validating" in API_CONTRACTS.md's stage vocabulary because
/// the reconciliation THROWs happen inside these calls, not as a separate step.
///
/// Each DimensionResult&lt;TControlTotals,TRow&gt; is serialized to a JsonElement rather than
/// passed through as `object` - Durable Task round-trips activity outputs through JSON, and a
/// generic type closed differently per dimension does not survive that as `object` (there is no
/// discriminator for the deserializer to pick the right closed type back up). CompositionAgent
/// only ever re-serializes whatever it is handed to JSON anyway (see its doc comment - "no shared
/// umbrella type... serialized straight to JSON"), so a JsonElement carries identical information
/// across the wire.
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

        var dimensionResults = new Dictionary<string, JsonElement>
        {
            ["Location"] = JsonSerializer.SerializeToElement(location),
            ["Entity"] = JsonSerializer.SerializeToElement(entity),
            ["Risk"] = JsonSerializer.SerializeToElement(risk),
            ["Nature"] = JsonSerializer.SerializeToElement(nature),
            ["Departments"] = JsonSerializer.SerializeToElement(departments),
            ["Act"] = JsonSerializer.SerializeToElement(act),
            ["Users"] = JsonSerializer.SerializeToElement(users),
            ["Internal"] = JsonSerializer.SerializeToElement(@internal),
            ["Event"] = JsonSerializer.SerializeToElement(@event),
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
