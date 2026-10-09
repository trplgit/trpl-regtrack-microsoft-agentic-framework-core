using System.Text.Json;
using Insights.Presentation;
using Xunit;

namespace Insights.UnitTests;

/// <summary>[ADDED 2026-10-09] ScopedPatchSplicer.BuildPlan / Apply and the TileCards keying they rely on.</summary>
public class ScopedPatchSplicerTests
{
    private const string Page =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>.card{padding:4px}</style><style>@font-face{font-family:P;src:url(data:,x)}</style></head><body>" +
        "<section class=\"card\" id=\"c0\"><h3>  Alpha\n  report </h3><p>12 items</p><button id=\"b0\">Go</button></section>" +
        "<section class=\"card\" id=\"c1\"><h3>Beta</h3><p>7 items</p></section>" +
        "<section class=\"card\" id=\"c2\"><h3>Gamma</h3><p>1 item</p></section>" +
        "<script type=\"application/json\" id=\"insights-data\">{\"rows\":[]}</script>" +
        "<script>function alphaDraw(){ document.getElementById('b0'); }</script>" +
        "<script>function unrelated(){ return 1; }</script></body></html>";

    private static TileFinding F(int ordinal, string title, TileFindingSeverity severity = TileFindingSeverity.Functional) =>
        new($"section.card[data-tile-qa-index=\"{ordinal}\"]", title, "click on button", severity, "does nothing", "", "", ordinal, "button");

    [Fact]
    public void Fingerprint_CollapsesWhitespace_AndCaps80()
    {
        Assert.Equal("Alpha report", TileCards.Fingerprint("  Alpha\n  report "));
        Assert.Equal(80, TileCards.Fingerprint(new string('x', 200)).Length);
        Assert.Equal(string.Empty, TileCards.Fingerprint(null));
    }

    [Fact]
    public void Fingerprints_UseHeadingThenWholeCard_InDocumentOrder()
    {
        var fps = TileCards.Fingerprints(Page + "<section class=\"card\">no heading here</section>");

        Assert.Equal([(0, "Alpha report"), (1, "Beta"), (2, "Gamma")], fps.Take(3).ToList());
    }

    [Fact]
    public void OrdinalOf_PrefersCardOrdinal_FallsBackToLegacySelector()
    {
        Assert.Equal(4, TileCards.OrdinalOf(F(4, "x")));
        var legacy = new TileFinding("section.card[data-tile-qa-index=\"2\"]", "x", "click", TileFindingSeverity.Cosmetic, "d", "b", "a");
        Assert.Equal(2, TileCards.OrdinalOf(legacy));
        Assert.Null(TileCards.OrdinalOf(legacy with { CardSelector = "section.card:nth-of-type(3)" }));
    }

    [Fact]
    public void BuildPlan_LocatesByOrdinalAndHeading_SendsOnlyTargetCards_NoDataBlock_NoFont()
    {
        var plan = ScopedPatchSplicer.BuildPlan(Page, [F(0, "Alpha report")], out var skip);

        Assert.Null(skip);
        Assert.NotNull(plan);
        Assert.Equal([0], plan!.TargetOrdinals);
        using var doc = JsonDocument.Parse(plan.RequestJson);
        var cards = doc.RootElement.GetProperty("cards");
        Assert.Equal(1, cards.GetArrayLength());
        Assert.Equal(0, cards[0].GetProperty("ordinal").GetInt32());
        Assert.Contains("12 items", cards[0].GetProperty("html").GetString());
        Assert.DoesNotContain("Beta", plan.RequestJson);
        Assert.DoesNotContain("insights-data", plan.RequestJson);
        Assert.DoesNotContain("@font-face", plan.RequestJson);
        Assert.Contains(".card{padding:4px}", doc.RootElement.GetProperty("page_styles").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("page_scripts").GetArrayLength());
    }

    [Fact]
    public void BuildPlan_HeadingMismatch_IsUnpatchable()
    {
        var plan = ScopedPatchSplicer.BuildPlan(Page, [F(0, "Something else"), F(1, "Beta")], out _);

        Assert.NotNull(plan);
        Assert.Equal([1], plan!.TargetOrdinals);
        Assert.Equal([0], plan.UnpatchableOrdinals);
    }

    [Fact]
    public void BuildPlan_NothingLocatable_ReturnsNullWithReason()
    {
        var plan = ScopedPatchSplicer.BuildPlan(Page, [F(7, "Nope")], out var skip);

        Assert.Null(plan);
        Assert.Contains("no finding could be matched", skip);
    }

    [Fact]
    public void BuildPlan_FunctionalFirst_ThenDocumentOrder_CappedAtFourCards()
    {
        var many = string.Concat(Enumerable.Range(0, 6).Select(i => $"<section class=\"card\"><h3>Card {i}</h3><p>{i}</p></section>"));
        var page = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" + many + "</body></html>";
        var findings = new List<TileFinding>
        {
            F(0, "Card 0", TileFindingSeverity.Cosmetic), F(1, "Card 1", TileFindingSeverity.Cosmetic), F(2, "Card 2", TileFindingSeverity.Cosmetic),
            F(3, "Card 3", TileFindingSeverity.Cosmetic), F(4, "Card 4"), F(5, "Card 5"),
        };

        var plan = ScopedPatchSplicer.BuildPlan(page, findings, out _);

        Assert.Equal([0, 1, 4, 5], plan!.TargetOrdinals); // the two Functional ones, then the first two Cosmetic, sorted
    }

    [Fact]
    public void BuildPlan_LegacySelectorFinding_IsLocated()
    {
        var legacy = new TileFinding("section.card[data-tile-qa-index=\"1\"]", "Beta", "click", TileFindingSeverity.Functional, "d", "b.png", "a.png");

        var plan = ScopedPatchSplicer.BuildPlan(Page, [legacy], out _);

        Assert.Equal([1], plan!.TargetOrdinals);
    }

    [Fact]
    public void Apply_ReplacesTheTargetCard_LeavesEverythingElseByteIdentical()
    {
        const string fixedCard = "<section class=\"card\" id=\"c1\"><h3>Beta</h3><p>7 items</p><script>document.addEventListener('DOMContentLoaded', function(){});</script></section>";
        var answer = "{\"cards\":[{\"ordinal\":1,\"html\":" + JsonSerializer.Serialize(fixedCard) + "}]}";

        var result = ScopedPatchSplicer.Apply(Page, answer, [1]);

        Assert.Equal("patched", result.Outcome);
        Assert.Equal([1], result.PatchedOrdinals);
        var beforeCards = TileCards.CardsOf(TileCards.Parse(Page)).Select(c => c.OuterHtml).ToList();
        var afterCards = TileCards.CardsOf(TileCards.Parse(result.Html)).Select(c => c.OuterHtml).ToList();
        Assert.Equal(beforeCards[0], afterCards[0]);
        Assert.Equal(beforeCards[2], afterCards[2]);
        Assert.Contains("DOMContentLoaded", afterCards[1]);
        Assert.Contains("function alphaDraw()", result.Html);
        Assert.Contains("id=\"insights-data\"", result.Html);
    }

    [Fact]
    public void Apply_NonTargetOrdinal_OutOfRange_TwoRoots_WrongRoot_AreRejected()
    {
        var answer = "{\"cards\":[" +
            "{\"ordinal\":2,\"html\":\"<section class=\\\"card\\\"><h3>Gamma</h3></section>\"}," +
            "{\"ordinal\":9,\"html\":\"<section class=\\\"card\\\"></section>\"}," +
            "{\"ordinal\":1,\"html\":\"<section class=\\\"card\\\"><h3>Beta</h3></section><p>stray</p>\"}," +
            "{\"ordinal\":0,\"html\":\"<div class=\\\"card\\\"><h3>Alpha</h3></div>\"}]}";

        var result = ScopedPatchSplicer.Apply(Page, answer, [0, 1]);

        Assert.Equal("rejected", result.Outcome);
        Assert.Equal(Page, result.Html);
        Assert.Contains(result.Rejections, r => r.Contains("card 2 was not a target"));
        Assert.Contains(result.Rejections, r => r.Contains("card 9 was not a target"));
        Assert.Contains(result.Rejections, r => r.Contains("card 1 must be exactly one element"));
        Assert.Contains(result.Rejections, r => r.Contains("card 0 root is <div"));
    }

    [Fact]
    public void Apply_IdenticalCard_IsUnchanged_NotRejected()
    {
        const string same = "<section class=\"card\" id=\"c1\"><h3>Beta</h3><p>7 items</p></section>";
        var answer = "{\"cards\":[{\"ordinal\":1,\"html\":" + JsonSerializer.Serialize(same) + "}]}";

        var result = ScopedPatchSplicer.Apply(Page, answer, [1]);

        Assert.Equal("unchanged", result.Outcome);
        Assert.Empty(result.Rejections);
    }

    [Fact]
    public void Apply_OversizedCard_IsRejected()
    {
        var huge = "<section class=\"card\" id=\"c1\"><h3>Beta</h3><p>7 items</p>" + new string('x', 20_000) + "</section>";
        var answer = "{\"cards\":[{\"ordinal\":1,\"html\":" + JsonSerializer.Serialize(huge) + "}]}";

        var result = ScopedPatchSplicer.Apply(Page, answer, [1]);

        Assert.Equal("rejected", result.Outcome);
        Assert.Contains(result.Rejections, r => r.Contains("returned") && r.Contains("allowed"));
    }

    [Fact]
    public void Apply_BadJson_IsRejected_FencedJsonIsAccepted()
    {
        Assert.Equal("rejected", ScopedPatchSplicer.Apply(Page, "<html>whole page</html>", [1]).Outcome);
        Assert.Equal("unchanged", ScopedPatchSplicer.Apply(Page, "```json\n{\"cards\":[],\"unfixable\":[{\"ordinal\":1,\"reason\":\"shared script\"}]}\n```", [1]).Outcome);
    }
}
