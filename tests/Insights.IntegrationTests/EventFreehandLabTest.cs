using Insights.Agents;
using Insights.Data;
using Insights.Presentation;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// [LAB, 2026-09-22] Proves the Event freehand composition+render wiring end to end (real
/// Fetch -> Compose -> Narrate(v2, production agent) -> Render -> PoppinsFontInjector), against
/// real data. NOT part of the automated suite - spends real LLM tokens. Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~EventFreehandLabTest
/// Requires: ConnectionStrings__RegTrack, MAF_ENDPOINT, MAF_MODEL, MAF_API_KEY.
/// Deleted after the report is generated and reviewed.
/// </summary>
public sealed class EventFreehandLabTest(ITestOutputHelper output)
{
    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this lab test.");

    [Fact]
    public Task GenerateRealEventReport_Tenant29() =>
        RunAsync(userId: 38, customerId: 29, label: "Tenant29");

    // Minda Corporation Group - user 9629 confirmed real scope on this tenant earlier this
    // session (Departments/Act, UAT box). Event uses the same tvfInsightsScopePairs gate as
    // every other dimension, so the same user should have real Event scope too.
    [Fact]
    public Task GenerateRealEventReport_Minda() =>
        RunAsync(userId: 9629, customerId: 1008, label: "Minda");

    private async Task RunAsync(int userId, int customerId, string label)
    {
        var connectionString = RequireEnv("ConnectionStrings__RegTrack");
        var endpoint = RequireEnv("MAF_ENDPOINT");
        var model = RequireEnv("MAF_MODEL");
        var apiKey = RequireEnv("MAF_API_KEY");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");

        var dimensionRepository = new SqlDimensionRepository(connectionString);
        var evt = await dimensionRepository.GetEventAsync(userId, customerId);
        output.WriteLine($"[{label}] Fetched {evt.Rows.Count} event-type rows, {evt.Assertions.Count} assertions, {evt.Findings.Count} findings.");
        output.WriteLine($"[{label}] ControlTotals: ScopedInstances={evt.ControlTotals.ScopedInstances} EventTypesReported={evt.ControlTotals.EventTypesReported} InstancesActiveInWindow={evt.ControlTotals.InstancesActiveInWindow} EventModuleDormant={evt.ControlTotals.EventModuleDormant} BranchesWithoutEventCoverage={evt.ControlTotals.BranchesWithoutEventCoverage}");
        foreach (var row in evt.Rows.OrderByDescending(r => r.InstanceCount).Take(10))
            output.WriteLine($"  {row.EventName}: InstanceCount={row.InstanceCount} DistinctStartDates={row.DistinctStartDates} InstancesSinceCutoff={row.InstancesSinceCutoff} Earliest={row.EarliestStart:d} Latest={row.LatestStart:d}");

        var rowsJson = System.Text.Json.JsonSerializer.Serialize(evt.Rows);
        var controlTotalsJson = System.Text.Json.JsonSerializer.Serialize(evt.ControlTotals);
        var dataQualityJson = System.Text.Json.JsonSerializer.Serialize(evt.DataQuality);

        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "02_composition_freehand_event.md"));
        var compositionAgent = new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "FreehandCompositionEventAgent", "Decides structure/hero/emphasis for a freehand Event insight from real tenant data.",
            compositionInstructions));
        var composeResult = await compositionAgent.ComposeAsync(evt.Assertions, evt.Findings, rowsJson, controlTotalsJson, dataQualityJson);
        var plan = composeResult.Value;
        output.WriteLine($"[{label}] Compose: {composeResult.TotalTokens} tokens. Hero: {plan.Hero.Block} - {plan.Hero.Reason}");
        foreach (var block in plan.Blocks)
            output.WriteLine($"  Block: {block.Block} - {block.Emphasis}");

        var narrateInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "v2/03_narrative_analyst.md"));
        var narrateAgent = new MafAnalystNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "AnalystNarrativeAgent", "Traces root cause from typed assertions and raw dimension rows.", narrateInstructions));
        var narrateResult = await narrateAgent.AnalyzeAndNarrateAsync(plan, evt.Assertions, evt.Findings, "Event", rowsJson, controlTotalsJson);
        output.WriteLine($"[{label}] Narrate: {narrateResult.TotalTokens} tokens.");
        foreach (var block in narrateResult.Value.Blocks)
        {
            if (block.Refused is not null)
                output.WriteLine($"  [{block.Block}] REFUSED - assertion_id={block.Refused.AssertionId} reason={block.Refused.Reason}");
            else
                output.WriteLine($"  [{block.Block}] {block.Prose}");
        }

        var renderInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "05_report_html_dimension_selection_event.md"));
        var renderAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "DimensionSelectionEventReportHtmlAgent", "Renders a freehand-composed Event insight as self-contained HTML.",
            renderInstructions));
        var renderResult = await renderAgent.RenderAsync(
            plan, narrateResult.Value, evt.Assertions, "Minda Corporation Group", "dimension_selection", DateTime.UtcNow,
            locationRows: null,
            dimensionRowsJson: new Dictionary<string, string> { ["Event"] = rowsJson },
            dimensionControlTotalsJson: new Dictionary<string, string> { ["Event"] = controlTotalsJson });
        output.WriteLine($"[{label}] Render: {renderResult.TotalTokens} tokens.");

        var html = PoppinsFontInjector.Inject(renderResult.Value);
        var outPath = $@"D:\trpl-reginsights-dev\local-report-event-{label.ToLowerInvariant()}.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"[{label}] Written: {outPath}");

        Assert.NotEmpty(narrateResult.Value.Blocks);
        Assert.Contains("<html", renderResult.Value, StringComparison.OrdinalIgnoreCase);
    }
}
