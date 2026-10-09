using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-10-09] The ONE definition of "which card is this" shared by the browser-side
/// checker (InteractiveTileChecker), the scoped patcher (ScopedPatchSplicer) and the integrity
/// gate (PatchIntegrityGate).
///
/// A card is identified by its static ORDINAL (position among <c>section.card</c> elements in an
/// AngleSharp parse of the exact page string) plus its FINGERPRINT (heading text, whitespace
/// collapsed, first 80 characters). The browser-assigned <c>data-tile-qa-index</c> that
/// TileFinding.CardSelector used to carry alone is a DOM-order index that only exists inside one
/// Playwright page; it is not in the stored HTML, so nothing outside that page could locate the
/// card. Ordinal + fingerprint is computable from the string alone, in C# and in page JS with the
/// same rule (<see cref="JsFingerprint"/>), and stays stable across patch attempts because the
/// gate forbids a patch from changing card count, order or heading.
/// </summary>
public static partial class TileCards
{
    public const string CardSelector = "section.card";
    public const string HeadingSelector = "h1,h2,h3,h4,.card-title";
    public const int FingerprintLength = 80;

    /// <summary>
    /// Page-side twin of <see cref="Fingerprint(string?)"/>: a JS arrow function taking the card
    /// element. Keep the two in step - a drift here silently makes every card "unmatched".
    /// </summary>
    public const string JsFingerprint =
        "(el => (((el.querySelector('h1,h2,h3,h4,.card-title') || el).textContent) || '').replace(/\\s+/g, ' ').trim().slice(0, 80))";

    public static string Fingerprint(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var collapsed = Whitespace().Replace(text, " ").Trim();
        return collapsed.Length <= FingerprintLength ? collapsed : collapsed[..FingerprintLength];
    }

    public static string FingerprintOf(IElement card) =>
        Fingerprint((card.QuerySelector(HeadingSelector) ?? card).TextContent);

    public static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    public static IReadOnlyList<IElement> CardsOf(IHtmlDocument document) => document.QuerySelectorAll(CardSelector).ToList();

    /// <summary>Static ordinals and fingerprints for a page string, in document order.</summary>
    public static IReadOnlyList<(int Ordinal, string Fingerprint)> Fingerprints(string html)
    {
        using var document = Parse(html);
        return CardsOf(document).Select((card, i) => (i, FingerprintOf(card))).ToList();
    }

    /// <summary>
    /// The ordinal a finding refers to: its own <see cref="TileFinding.CardOrdinal"/>, or for a
    /// finding recorded before that field existed, the <c>data-tile-qa-index</c> in its legacy
    /// selector (which equalled the document-order index on the page it came from). Null when
    /// neither is available.
    /// </summary>
    public static int? OrdinalOf(TileFinding finding)
    {
        if (finding.CardOrdinal is { } ordinal) return ordinal;
        var match = LegacySelectorIndex().Match(finding.CardSelector ?? string.Empty);
        return match.Success && int.TryParse(match.Groups["i"].Value, out var parsed) ? parsed : null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"data-tile-qa-index\s*=\s*[""']?(?<i>\d+)")]
    private static partial Regex LegacySelectorIndex();
}
