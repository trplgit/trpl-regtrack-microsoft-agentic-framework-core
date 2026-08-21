using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;
using Xunit;

namespace Insights.UnitTests;

public class NormalizeActivityTests
{
    // Matches ReportEmitNormalizerTests.ValidDocument exactly - same known-good shape, so this
    // test's "approved" case rests on ground already proven by that file, not a hand-rolled guess.
    private const string ValidDocument = """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8">
        <style>body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }</style>
        </head>
        <body>
        <h1>Compliance Health Report</h1>
        <svg role="img"><title>Overdue by location</title><rect width="10" height="10"/></svg>
        <script>console.log('static, no network calls');</script>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_ValidSelfContainedHtml_ReturnsIt()
    {
        var activity = new NormalizeActivity();

        var result = await activity.RunAsync(new NormalizeInput(ValidDocument));

        Assert.Equal(ValidDocument, result.Html);
    }

    [Fact]
    public async Task RunAsync_ExternalScriptSrc_ThrowsNotNormalizable()
    {
        var activity = new NormalizeActivity();
        var html = ValidDocument.Replace(
            "<script>", "<script src=\"https://cdn.example.com/chart.js\"></script><script>");

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() =>
            activity.RunAsync(new NormalizeInput(html)));
        Assert.Equal("NOT_NORMALIZABLE", ex.ReasonCode);
    }
}
