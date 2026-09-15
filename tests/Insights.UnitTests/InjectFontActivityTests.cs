using Insights.Worker.Orchestration.Activities;
using Xunit;

namespace Insights.UnitTests;

public class InjectFontActivityTests
{
    private const string ValidDocument = """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8">
        <style>body { font-family: 'Poppins', sans-serif; }</style>
        </head>
        <body>
        <h1>Compliance Health Report</h1>
        </body></html>
        """;

    [Fact]
    public async Task RunAsync_ValidDocument_ReturnsHtmlWithFontFaceInjected()
    {
        var activity = new InjectFontActivity();

        var result = await activity.RunAsync(new InjectFontInput(ValidDocument));

        Assert.Contains("@font-face", result.Html, StringComparison.Ordinal);
        // Original content preserved, just extended.
        Assert.Contains("Compliance Health Report", result.Html, StringComparison.Ordinal);
    }
}
