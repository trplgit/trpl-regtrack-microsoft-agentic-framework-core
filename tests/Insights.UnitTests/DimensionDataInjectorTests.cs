using System.Text.Json;
using Insights.Presentation;

namespace Insights.UnitTests;

public sealed class DimensionDataInjectorTests
{
    private const string Rows = """[{"UserID":1,"UserName":"A<B & C>","Instances":5},{"UserID":2,"UserName":"Zed","Instances":0}]""";
    private const string Totals = """{"ScopedInstances":5}""";

    private static string DataBlockText(string html)
    {
        const string open = "<script type=\"application/json\" id=\"insights-data\">";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "data block missing");
        start += open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    [Fact]
    public void PutsTheDataBlockBeforeTheFirstScript_SoChartCodeCanReadIt()
    {
        const string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><h1>R</h1><script>draw()</script></body></html>";

        var result = DimensionDataInjector.Inject(html, "Users", Rows, Totals);

        Assert.True(result.IndexOf("id=\"insights-data\"", StringComparison.Ordinal) < result.IndexOf("<script>draw()", StringComparison.Ordinal));
    }

    [Fact]
    public void CarriesEveryRowExactly_AndTheTotals()
    {
        var result = DimensionDataInjector.Inject("<html><body><script>x()</script></body></html>", "Users", Rows, Totals);

        using var doc = JsonDocument.Parse(DataBlockText(result));
        Assert.Equal("Users", doc.RootElement.GetProperty("dimension").GetString());
        var rows = doc.RootElement.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("A<B & C>", rows[0].GetProperty("UserName").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("totals").GetProperty("ScopedInstances").GetInt32());
    }

    /// <summary>Tenant text must never be able to close the script or look like a tag to the sanitizer.</summary>
    [Fact]
    public void EscapesAngleBracketsAndAmpersandsInsideTheBlock()
    {
        const string hostileRows = """[{"UserName":"</script><img src=x onerror=alert(1)>"}]""";

        var result = DimensionDataInjector.Inject("<html><body></body></html>", "Users", hostileRows, "{}");
        var block = DataBlockText(result);

        Assert.DoesNotContain("<", block);
        Assert.DoesNotContain(">", block);
        Assert.DoesNotContain("&", block);
        using var doc = JsonDocument.Parse(block);
        Assert.Equal("</script><img src=x onerror=alert(1)>", doc.RootElement.GetProperty("rows")[0].GetProperty("UserName").GetString());
    }

    [Fact]
    public void WithNoScript_GoesBeforeTheClosingBody()
    {
        var result = DimensionDataInjector.Inject("<html><body><p>t</p></body></html>", "Act", "[]", null);

        Assert.True(result.IndexOf("id=\"insights-data\"", StringComparison.Ordinal) < result.IndexOf("</body>", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(DataBlockText(result));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("totals").ValueKind);
    }

    [Fact]
    public void ReplacesAnyDataBlockTheModelWroteItself()
    {
        const string html = "<html><body><script type=\"application/json\" id=\"insights-data\">[{\"fake\":1}]</script><script>x()</script></body></html>";

        var result = DimensionDataInjector.Inject(html, "Users", Rows, Totals);

        Assert.Equal(1, result.Split("id=\"insights-data\"").Length - 1);
        Assert.DoesNotContain("fake", result);
    }

    /// <summary>[ADDED 2026-10-09, FOUND LIVE] The tile-QA patch loop must never hand the row JSON
    /// to the patch LLM - three real reports came back with "rows":[]. Strip removes the whole
    /// block, every copy, and nothing else.</summary>
    [Fact]
    public void Strip_RemovesTheDataBlock_AndNothingElse()
    {
        const string page = "<html><body><h1>R</h1><script>draw()</script></body></html>";
        var injected = DimensionDataInjector.Inject(page, "Users", Rows, Totals);
        var twice = injected.Replace("</body>", "<script id=\"insights-data\" type=\"application/json\">{\"rows\":[]}</script></body>");

        var stripped = DimensionDataInjector.Strip(twice);

        Assert.Equal(page, stripped);
        Assert.DoesNotContain("insights-data", stripped);
    }

    [Fact]
    public void Strip_ThenInject_RoundTripsEveryRow()
    {
        var injected = DimensionDataInjector.Inject("<html><body><script>x()</script></body></html>", "Users", Rows, Totals);

        var restored = DimensionDataInjector.Inject(DimensionDataInjector.Strip(injected), "Users", Rows, Totals);

        Assert.Equal(injected, restored);
        Assert.False(DimensionDataInjector.RowsLost(restored, Rows));
    }

    /// <summary>The live failure shape: rows were expected, the page's block says "rows":[].</summary>
    [Fact]
    public void RowsLost_WhenRowsWereExpected_AndTheBlockHasAnEmptyArray()
    {
        const string page = "<html><body><script type=\"application/json\" id=\"insights-data\">{\"dimension\":\"Users\",\"rows\":[],\"totals\":{\"UsersReported\":160}}</script><script>draw()</script></body></html>";

        Assert.True(DimensionDataInjector.RowsLost(page, Rows));
    }

    [Fact]
    public void RowsLost_WhenRowsWereExpected_AndTheBlockIsMissingOrUnparsable()
    {
        Assert.True(DimensionDataInjector.RowsLost("<html><body><script>draw()</script></body></html>", Rows));
        Assert.True(DimensionDataInjector.RowsLost("<html><body><script type=\"application/json\" id=\"insights-data\">{not json</script></body></html>", Rows));
    }

    [Fact]
    public void RowsLost_IsFalse_WhenThePageCarriesTheRows()
    {
        var page = DimensionDataInjector.Inject("<html><body><script>x()</script></body></html>", "Users", Rows, Totals);

        Assert.False(DimensionDataInjector.RowsLost(page, Rows));
    }

    /// <summary>No rows expected (fixed_holistic, or a dimension that genuinely returned none) can
    /// never be "lost" - the guard must stay silent for every page that never had a block.</summary>
    [Fact]
    public void RowsLost_IsFalse_WhenNoRowsWereExpected()
    {
        const string bare = "<html><body><script>draw()</script></body></html>";

        Assert.False(DimensionDataInjector.RowsLost(bare, null));
        Assert.False(DimensionDataInjector.RowsLost(bare, ""));
        Assert.False(DimensionDataInjector.RowsLost(bare, "[]"));
        Assert.False(DimensionDataInjector.RowsLost(DimensionDataInjector.Inject(bare, "Act", "[]", null), "[]"));
    }
}
