using Insights.Presentation;
using Xunit;

namespace Insights.UnitTests;

/// <summary>[ADDED 2026-10-09] One case per check in PatchIntegrityGate, plus the valid handler-only change that must pass.</summary>
public class PatchIntegrityGateTests
{
    private const string Head = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>.card{padding:4px}</style></head><body>";
    private const string Tail = "<script>function draw(){ clear(document.getElementById('timeline')); }</script></body></html>";
    private const string Card0 = "<section class=\"card\" id=\"c0\"><h3>Alpha 2026</h3><p>3,594 obligations, 40.7% overdue</p><button id=\"b0\">Go</button></section>";
    private const string Card1 = "<section class=\"card\" id=\"c1\"><h3>Timeline</h3><div id=\"timeline\"></div></section>";
    private static readonly string Before = Head + Card0 + Card1 + Tail;

    private static string With(string card0 = Card0, string card1 = Card1, string tail = Tail, string head = Head) => head + card0 + card1 + tail;

    [Fact]
    public void ValidHandlerOnlyChange_Passes()
    {
        var after = With(card0: Card0.Replace("</section>",
            "<script>document.addEventListener('DOMContentLoaded', function () { setTimeout(function(){}, 50); });</script></section>"));

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        Assert.True(result.Ok, string.Join("; ", result.Violations));
    }

    [Fact]
    public void RemovedContainer_LikeTheLiveActTimeline_Fails()
    {
        var after = With(card1: "<section class=\"card\" id=\"c1\"><h3>Timeline</h3></section>");

        var result = PatchIntegrityGate.Check(Before, after, [1]);

        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Contains("'timeline' disappeared"));
    }

    [Fact]
    public void ChangedNumber_Fails()
    {
        var after = With(card0: Card0.Replace("40.7%", "41.7%"));

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        Assert.Contains(result.Violations, v => v.Contains("numbers changed"));
    }

    [Fact]
    public void ChangedHeading_Fails()
    {
        var after = With(card0: Card0.Replace("Alpha 2026", "Alpha 2027 revised"));

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        Assert.Contains(result.Violations, v => v.Contains("heading changed"));
    }

    [Fact]
    public void DuplicateId_Fails()
    {
        var after = With(card0: Card0.Replace("<button id=\"b0\">", "<button id=\"b0\"><span id=\"c1\"></span>"));

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        Assert.Contains(result.Violations, v => v.Contains("'c1' is no longer unique"));
    }

    [Fact]
    public void InlineHandler_Fails()
    {
        var after = With(card0: Card0.Replace("<button id=\"b0\">", "<button id=\"b0\" onclick=\"go()\">"));

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        Assert.Contains(result.Violations, v => v.Contains("inline handler"));
    }

    [Fact]
    public void UntouchedCardChanged_Fails()
    {
        var after = With(card1: Card1.Replace("<h3>Timeline</h3>", "<h3>Timeline!</h3>"));

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        Assert.Contains(result.Violations, v => v.Contains("card 1 was not a patch target but changed"));
    }

    [Fact]
    public void CardCountChanged_Fails()
    {
        var after = With(card1: string.Empty);

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        Assert.Contains(result.Violations, v => v.Contains("card count changed"));
    }

    [Fact]
    public void PageLevelScriptChanged_Fails()
    {
        var after = With(tail: Tail.Replace("function draw()", "function draw2()"));

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        Assert.Contains(result.Violations, v => v.Contains("page-level style/script blocks changed"));
    }

    [Fact]
    public void ExternalScriptOrJsonBlockOrFontInsideCard_Fails()
    {
        var after = With(card0: Card0.Replace("</section>",
            "<script src=\"https://cdn.example/x.js\"></script><script type=\"application/json\" id=\"insights-data\">{}</script><style>@font-face{font-family:P}</style></section>"));

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        Assert.Contains(result.Violations, v => v.Contains("external script"));
        Assert.Contains(result.Violations, v => v.Contains("JSON script block"));
        Assert.Contains(result.Violations, v => v.Contains("insights-data"));
        Assert.Contains(result.Violations, v => v.Contains("font block"));
    }

    [Fact]
    public void NestedCard_Fails()
    {
        var after = With(card0: Card0.Replace("</section>", "<section class=\"card\"><h3>Inner</h3></section></section>"));

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        // A nested section.card is itself counted as a card, so the count check fires first; either
        // violation proves the patch cannot land.
        Assert.False(result.Ok);
        Assert.Contains(result.Violations, v => v.Contains("nested card") || v.Contains("card count changed"));
    }

    [Fact]
    public void TwoCardLocalScripts_Fails()
    {
        var after = With(card0: Card0.Replace("</section>", "<script>1</script><script>2</script></section>"));

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        Assert.Contains(result.Violations, v => v.Contains("more than one card-local script"));
    }

    [Fact]
    public void NumbersInsideACardLocalScript_AreNotVisibleNumbers()
    {
        var after = With(card0: Card0.Replace("</section>", "<script>document.addEventListener('DOMContentLoaded', function(){ var ms = 250; var pct = 99.9; });</script></section>"));

        var result = PatchIntegrityGate.Check(Before, after, [0]);

        Assert.DoesNotContain(result.Violations, v => v.Contains("numbers changed"));
    }

    [Fact]
    public void EmitNormalizerViolationIntroducedByThePatch_Fails_PreExistingOnesDoNot()
    {
        // Pre-existing: no charset meta on either side -> not the patch's fault.
        var noCharsetHead = Head.Replace("<meta charset=\"utf-8\">", string.Empty);
        var before = With(head: noCharsetHead);
        var after = With(head: noCharsetHead, card0: Card0.Replace("</section>",
            "<script>document.addEventListener('DOMContentLoaded', function(){ fetch('https://x/y'); });</script></section>"));

        var result = PatchIntegrityGate.Check(before, after, [0]);

        Assert.Contains(result.Violations, v => v.Contains("emit normalizer") && v.Contains("fetch"));
        Assert.DoesNotContain(result.Violations, v => v.Contains("charset"));
    }

    [Fact]
    public void NumbersOf_ExtractsIndianGroupedAndPercentTokens()
    {
        Assert.Equal(["12", "3,594", "40.7%"], PatchIntegrityGate.NumbersOf("3,594 obligations, 40.7% overdue, 12 acts"));
    }
}
