using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// [LAB, 2026-09-22] Proves ReadOnlySqlFetchTool end to end, now widened for real cross-dimension
/// tracing (DepartmentID/DepartmentName/OwnerClass added to #scoped). Deliberately uses the UAT
/// connection (sa) - the tool's own query-shape validation is the only backstop there, confirmed
/// and accepted earlier this session. NOT part of the automated suite - spends real LLM tokens.
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~SqlFetchToolLabTest
/// Config: reads D:\trpl-reginsights-dev\appsettings.uat.json directly (ConnectionStrings:RegTrack,
/// Llm:Maf:Endpoint/Model/ApiKey), falling back to env vars of the same shape - avoids ever putting
/// real credentials in a shell command line. Deleted after reviewed.
/// </summary>
public sealed class SqlFetchToolLabTest(ITestOutputHelper output)
{
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.uat.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    private static string RequireConfig(string key) =>
        Config[key]
        ?? throw new InvalidOperationException($"Set '{key}' in appsettings.uat.json or as an env var before running this lab test.");

    [Fact]
    public async Task NarrateAndRenderWithSqlToolAvailable_Internal_Tenant29() =>
        await RunAsync("Internal", userId: 38, customerId: 29, label: "Tenant29");

    [Fact]
    public async Task NarrateAndRenderWithSqlToolAvailable_Event_Tenant29() =>
        await RunAsync("Event", userId: 38, customerId: 29, label: "Tenant29");

    private async Task RunAsync(string dimension, int userId, int customerId, string label)
    {
        var connectionString = RequireConfig("ConnectionStrings:RegTrack");
        var endpoint = RequireConfig("Llm:Maf:Endpoint");
        var model = RequireConfig("Llm:Maf:Model");
        var apiKey = RequireConfig("Llm:Maf:ApiKey");
        var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");

        var dimensionRepository = new SqlDimensionRepository(connectionString);
        var (rowsJson, controlTotalsJson, dataQualityJson, assertions, findings, rowCount) =
            await FetchAsync(dimensionRepository, dimension, userId, customerId);
        output.WriteLine($"[{label}/{dimension}] Fetched {rowCount} rows, {assertions.Count} assertions.");

        var compositionPromptFile = $"02_composition_freehand_{dimension.ToLowerInvariant()}.md";
        var compositionInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, compositionPromptFile));
        var compositionAgent = new MafFreehandDimensionCompositionAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, $"FreehandComposition{dimension}Agent", $"Decides structure/hero/emphasis for a freehand {dimension} insight from real tenant data.",
            compositionInstructions));
        var composeResult = await compositionAgent.ComposeAsync(assertions, findings, rowsJson, controlTotalsJson, dataQualityJson);
        var plan = composeResult.Value;
        output.WriteLine($"[{label}/{dimension}] Compose: {composeResult.TotalTokens} tokens.");

        var narrateInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, "v2/03_narrative_analyst.md"));
        var narrateAgent = new MafAnalystNarrativeAgent(MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "AnalystNarrativeAgent", "Traces root cause from typed assertions and raw dimension rows.", narrateInstructions),
            readOnlySqlConnectionString: connectionString);

        var narrateResult = await narrateAgent.AnalyzeAndNarrateAsync(
            plan, assertions, findings, dimension, rowsJson, controlTotalsJson,
            userId: userId, customerId: customerId);
        output.WriteLine($"[{label}/{dimension}] Narrate: {narrateResult.TotalTokens} tokens.");
        foreach (var block in narrateResult.Value.Blocks)
        {
            if (block.Refused is not null)
                output.WriteLine($"  [{block.Block}] REFUSED - assertion_id={block.Refused.AssertionId} reason={block.Refused.Reason}");
            else
            {
                output.WriteLine($"  [{block.Block}] {block.Prose}");
                output.WriteLine($"    row_refs_used: {string.Join(", ", block.RowRefsUsed?.Select(r => $"{r.Dimension}:{r.MemberKey}") ?? [])}");
            }
        }

        var renderPromptFile = $"05_report_html_dimension_selection_{dimension.ToLowerInvariant()}.md";
        var renderInstructions = await File.ReadAllTextAsync(Path.Combine(promptsDir, renderPromptFile));
        var renderAgent = new MafReportHtmlAgent(MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, $"DimensionSelection{dimension}ReportHtmlAgent", $"Renders a freehand-composed {dimension} insight as self-contained HTML.",
            renderInstructions));
        var renderResult = await renderAgent.RenderAsync(
            plan, narrateResult.Value, assertions, "Tenant29 Test Co", "dimension_selection", DateTime.UtcNow,
            locationRows: null,
            dimensionRowsJson: new Dictionary<string, string> { [dimension] = rowsJson },
            dimensionControlTotalsJson: new Dictionary<string, string> { [dimension] = controlTotalsJson });
        output.WriteLine($"[{label}/{dimension}] Render: {renderResult.TotalTokens} tokens.");

        var html = PoppinsFontInjector.Inject(renderResult.Value);
        var outPath = $@"D:\trpl-reginsights-dev\local-report-{dimension.ToLowerInvariant()}-sqltool-{label.ToLowerInvariant()}.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"[{label}/{dimension}] Written: {outPath}");

        Assert.NotEmpty(narrateResult.Value.Blocks);
    }

    private static async Task<(string RowsJson, string ControlTotalsJson, string DataQualityJson, IReadOnlyList<Assertion> Assertions, IReadOnlyList<Finding> Findings, int RowCount)>
        FetchAsync(IDimensionRepository repository, string dimension, int userId, int customerId)
    {
        switch (dimension)
        {
            case "Internal":
            {
                var r = await repository.GetInternalAsync(userId, customerId);
                return (System.Text.Json.JsonSerializer.Serialize(r.Rows), System.Text.Json.JsonSerializer.Serialize(r.ControlTotals), System.Text.Json.JsonSerializer.Serialize(r.DataQuality), r.Assertions, r.Findings, r.Rows.Count);
            }
            case "Event":
            {
                var r = await repository.GetEventAsync(userId, customerId);
                return (System.Text.Json.JsonSerializer.Serialize(r.Rows), System.Text.Json.JsonSerializer.Serialize(r.ControlTotals), System.Text.Json.JsonSerializer.Serialize(r.DataQuality), r.Assertions, r.Findings, r.Rows.Count);
            }
            default:
                throw new NotSupportedException($"Lab test does not wire up dimension '{dimension}' - add a case here.");
        }
    }
}
