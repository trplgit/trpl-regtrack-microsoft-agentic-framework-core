using Insights.Agents;
using Insights.Presentation;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// [REWORKED 2026-10-09] PatchRenderActivity now sends the model only the failing card(s) and
/// splices its JSON answer back under a deterministic integrity gate. Every outcome is a return
/// value, never an exception.
/// </summary>
public class PatchRenderActivityTests
{
    private const string Page =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>.card{padding:4px}@font-face{font-family:P;src:url(data:font/woff2;base64,AAAA)}</style></head><body>" +
        "<section class=\"card\" id=\"c0\"><h3>Alpha</h3><p>12 items</p><button id=\"b0\">Go</button></section>" +
        "<section class=\"card\" id=\"c1\"><h3>Beta</h3><p>7 items</p></section>" +
        "<script type=\"application/json\" id=\"insights-data\">{\"rows\":[{\"x\":1}]}</script>" +
        "<script>function draw(){ return 99; }</script></body></html>";

    private const string FixedCard0 =
        "<section class=\"card\" id=\"c0\"><h3>Alpha</h3><p>12 items</p><button id=\"b0\">Go</button>" +
        "<script>document.addEventListener('DOMContentLoaded', function () { var c = document.getElementById('c0'); });</script></section>";

    private static readonly TileFinding Finding = new(
        "section.card[data-tile-qa-index=\"0\"]", "Alpha", "click on button", TileFindingSeverity.Functional, "does nothing", "", "", CardOrdinal: 0, ElementKind: "button");

    private static readonly StaticOptionsMonitor<TileQaOptions> PatchOn = new(new TileQaOptions { Enabled = true, PatchEnabled = true });

    private static (PatchRenderActivity Activity, Mock<IPatchRenderAgent> Agent, List<string> Requests) Build(string? answer, Exception? throws = null, TileQaOptions? options = null)
    {
        var requests = new List<string>();
        var agent = new Mock<IPatchRenderAgent>();
        var setup = agent.Setup(a => a.PatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((r, _) => requests.Add(r));
        if (throws is not null) setup.ThrowsAsync(throws);
        else setup.ReturnsAsync(new AgentCallResult<string>(answer ?? "{}", 500));
        var activity = new PatchRenderActivity(agent.Object, NullLogger<PatchRenderActivity>.Instance,
            options is null ? PatchOn : new StaticOptionsMonitor<TileQaOptions>(options));
        return (activity, agent, requests);
    }

    private static string Answer(string cardHtml) =>
        "{\"cards\":[{\"ordinal\":0,\"html\":" + System.Text.Json.JsonSerializer.Serialize(cardHtml) + "}],\"unfixable\":[]}";

    [Fact]
    public async Task Patched_SplicesOnlyTheTargetCard_AndReportsOrdinals()
    {
        var (activity, _, requests) = Build(Answer(FixedCard0));

        var result = await activity.RunAsync(new PatchRenderInput(Page, [Finding]));

        Assert.Equal("patched", result.Outcome);
        Assert.Equal([0], result.PatchedCardOrdinals);
        Assert.Equal(500, result.TotalTokens);
        Assert.Contains("DOMContentLoaded", result.Html);
        Assert.Contains("<h3>Beta</h3><p>7 items</p>", result.Html);
        Assert.Contains("function draw(){ return 99; }", result.Html);
        // The model saw the failing card and the page's scripts/styles - never the other card, the data block or the font.
        var request = Assert.Single(requests);
        Assert.Contains("Alpha", request);
        Assert.DoesNotContain("Beta", request);
        Assert.DoesNotContain("insights-data", request);
        Assert.DoesNotContain("@font-face", request);
        Assert.Contains("function draw()", request);
    }

    [Fact]
    public async Task Unchanged_WhenTheModelReturnsTheSameCard()
    {
        const string same = "<section class=\"card\" id=\"c0\"><h3>Alpha</h3><p>12 items</p><button id=\"b0\">Go</button></section>";
        var (activity, _, _) = Build(Answer(same));

        var result = await activity.RunAsync(new PatchRenderInput(Page, [Finding]));

        Assert.Equal("unchanged", result.Outcome);
        Assert.Equal(Page, result.Html);
        Assert.Null(result.PatchedCardOrdinals);
    }

    [Fact]
    public async Task Rejected_WhenTheIntegrityGateFails_InputPageIsKept()
    {
        // The model "fixed" the card by changing a figure and adding an inline handler - both forbidden.
        const string bad = "<section class=\"card\" id=\"c0\"><h3>Alpha</h3><p>13 items</p><button id=\"b0\" onclick=\"go()\">Go</button></section>";
        var (activity, _, _) = Build(Answer(bad));

        var result = await activity.RunAsync(new PatchRenderInput(Page, [Finding]));

        Assert.Equal("rejected", result.Outcome);
        Assert.Equal(Page, result.Html);
        Assert.Contains("numbers changed", result.RejectReason);
        Assert.Contains("inline handler", result.RejectReason);
    }

    [Fact]
    public async Task Rejected_WhenTheAnswerIsNotJson()
    {
        var (activity, _, _) = Build("<section class=\"card\">whole page nonsense</section>");

        var result = await activity.RunAsync(new PatchRenderInput(Page, [Finding]));

        Assert.Equal("rejected", result.Outcome);
        Assert.Equal(Page, result.Html);
    }

    [Fact]
    public async Task Failed_WhenTheAgentThrows_NeverPropagates()
    {
        var (activity, _, _) = Build(null, throws: new TimeoutException("model took too long"));

        var result = await activity.RunAsync(new PatchRenderInput(Page, [Finding]));

        Assert.Equal("failed", result.Outcome);
        Assert.Equal(Page, result.Html);
        Assert.Equal(0, result.TotalTokens);
        Assert.Contains("TimeoutException", result.RejectReason);
    }

    [Fact]
    public async Task Skipped_WhenNoFindingMapsToACard_AgentNeverCalled()
    {
        var (activity, agent, _) = Build(Answer(FixedCard0));
        var unlocatable = Finding with { CardOrdinal = 9, CardSelector = "section.card[data-tile-qa-index=\"9\"]" };

        var result = await activity.RunAsync(new PatchRenderInput(Page, [unlocatable]));

        Assert.Equal("skipped", result.Outcome);
        Assert.Equal(Page, result.Html);
        agent.Verify(a => a.PatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Disabled_WhenPatchingIsOffByConfiguration_AgentNeverCalled()
    {
        var (activity, agent, _) = Build(Answer(FixedCard0), options: new TileQaOptions { Enabled = true, PatchEnabled = false });

        var result = await activity.RunAsync(new PatchRenderInput(Page, [Finding]));

        Assert.Equal("disabled", result.Outcome);
        Assert.Equal(Page, result.Html);
        agent.Verify(a => a.PatchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoOptionsConfigured_IsDisabledByDefault()
    {
        var agent = new Mock<IPatchRenderAgent>(MockBehavior.Strict);
        var activity = new PatchRenderActivity(agent.Object, NullLogger<PatchRenderActivity>.Instance);

        var result = await activity.RunAsync(new PatchRenderInput(Page, [Finding]));

        Assert.Equal("disabled", result.Outcome);
    }

    /// <summary>A finding recorded by 4.8 carries only the legacy selector; it still locates the card.</summary>
    [Fact]
    public async Task LegacyFindingWithoutOrdinal_IsLocatedBySelectorIndexAndHeading()
    {
        var legacy = new TileFinding("section.card[data-tile-qa-index=\"0\"]", "Alpha", "click on button", TileFindingSeverity.Functional, "does nothing", "b.png", "a.png");
        var (activity, _, _) = Build(Answer(FixedCard0));

        var result = await activity.RunAsync(new PatchRenderInput(Page, [legacy]));

        Assert.Equal("patched", result.Outcome);
    }
}
