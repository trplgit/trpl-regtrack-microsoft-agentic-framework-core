using Insights.Agents;
using Insights.Domain;

namespace Insights.UnitTests;

public sealed class ReasoningExplainerAgentTests
{
    private static readonly CompositionPlan Plan = new(new CompositionHero("snapshot", "why"), [], [], []);

    private static ReasoningTraceBundle SingleDimensionBundle() => new(
        "Act", "run-1", Plan, [], [], "[{\"ActID\":1}]", "{\"ScopedInstances\":10}", [], [], []);

    [Fact]
    public void BuildPayload_SingleDimension_UsesTheOriginalDimensionRowsShape()
    {
        var payload = MafReasoningExplainerAgent.BuildPayload(SingleDimensionBundle());

        Assert.Contains("\"dimension_rows\": [{\"ActID\":1}]", payload);
        Assert.Contains("\"dimension_control_totals\": {\"ScopedInstances\":10}", payload);
        Assert.DoesNotContain("dimension_data", payload);
    }

    [Fact]
    public void BuildPayload_MultiDimension_UsesDimensionDataKeyedByName_NotTheSingularFields()
    {
        var bundle = new ReasoningTraceBundle(
            "Entity", "run-2", Plan, [], [], "[]", null, [], [], [],
            AllDimensionDataJson: new Dictionary<string, string>
            {
                ["Location"] = """{"Rows":[{"BranchID":1}],"ControlTotals":{"ScopedInstances":19}}""",
                ["Licence"] = """{"Rows":[{"LicenseTypeID":2}],"ControlTotals":{"ScopedLicences":108}}""",
            });

        var payload = MafReasoningExplainerAgent.BuildPayload(bundle);

        Assert.Contains("\"dimension_data\"", payload);
        Assert.Contains("\"Location\"", payload);
        Assert.Contains("\"BranchID\":1", payload);
        Assert.Contains("\"Licence\"", payload);
        Assert.Contains("\"ScopedLicences\":108", payload);
        Assert.DoesNotContain("\"dimension_rows\"", payload);
        Assert.DoesNotContain("\"dimension_control_totals\"", payload);

        // Real JSON, not just string concatenation - this must actually parse.
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        Assert.True(doc.RootElement.GetProperty("dimension_data").TryGetProperty("Location", out _));
    }
}
