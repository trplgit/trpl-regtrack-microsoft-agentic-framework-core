using Insights.Worker.Orchestration.Activities;

namespace Insights.UnitTests;

public sealed class BuildReasoningTraceActivityTests
{
    [Fact]
    public void AppendCompletenessCheck_ListsNumbersTheExplainerSkipped()
    {
        var result = BuildReasoningTraceActivity.AppendCompletenessCheck(
            "# File\n28 / 33 x 100 = 84.8%", ["28", "33", "84.8%", "1,102"]);

        // [2026-09-29] The file is plain text (served as .txt) - no Markdown heading marks.
        Assert.Contains("NUMBERS NOT EXPLAINED ABOVE - PLEASE CHECK THESE BY HAND", result);
        Assert.DoesNotContain("#", result.Substring(result.IndexOf("NUMBERS NOT EXPLAINED", StringComparison.Ordinal)));
        Assert.Contains("- 1,102", result);
        Assert.DoesNotContain("- 84.8%", result);
    }

    [Fact]
    public void AppendCompletenessCheck_SaysSoWhenEverythingIsCovered()
    {
        var result = BuildReasoningTraceActivity.AppendCompletenessCheck("28 of 33 = 84.80%", ["28", "33", "84.8%"]);

        Assert.Contains("CHECK DONE BY CODE", result);
        Assert.Contains("All 3 numbers shown on the report appear in this file.", result);
        Assert.DoesNotContain("##", result);
    }

    [Fact]
    public void AppendCompletenessCheck_LeavesTheFileAloneWithoutAReport()
    {
        Assert.Equal("text", BuildReasoningTraceActivity.AppendCompletenessCheck("text", null));
    }

    [Fact]
    public void ToolCallsWithoutSql_KeepsWhatHappenedButNeverTheQueryText()
    {
        var calls = new[]
        {
            new Insights.Data.ToolInvocationLogEntry("analyze_and_narrate", "fetch_scoped_sql_data",
                "SELECT COUNT(*) FROM #scoped", true, 120, DateTime.UtcNow),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(Insights.Agents.MafReasoningExplainerAgent.ToolCallsWithoutSql(calls));

        Assert.Contains("fetch_scoped_sql_data", json);
        Assert.Contains("120", json);
        Assert.DoesNotContain("SELECT", json);
    }
}
