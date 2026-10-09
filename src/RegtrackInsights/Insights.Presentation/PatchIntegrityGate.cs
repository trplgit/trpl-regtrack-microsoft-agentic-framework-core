using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace Insights.Presentation;

public sealed record PatchIntegrityResult(bool Ok, IReadOnlyList<string> Violations)
{
    public static readonly PatchIntegrityResult Pass = new(true, []);
}

/// <summary>
/// [ADDED 2026-10-09, FOUND LIVE] Deterministic before/after check on a patched page. Runs inside
/// PatchRenderActivity (never inline in the orchestrator - an inline decision on data that recorded
/// 4.8 histories already carry would replay differently). A patch is accepted only if it changed
/// NOTHING outside the cards it was asked to fix and kept every id, heading and number inside them.
///
/// Why each check exists:
///  (a) card count equal, untouched cards byte-identical - the whole-page patch call used to
///      "preserve everything else" by reproducing it, and reproduced it wrong;
///  (b) every id that existed still exists, ids unique - a real Act report lost #timeline and
///      threw a TypeError on load, shipped as complete;
///  (c) page-level style/script blocks unchanged - the chart code and the shared theme are the
///      largest opaque payloads on the page and the most likely to be mangled;
///  (d) per patched card: one root section.card, no nested card, same heading fingerprint, same
///      multiset of numbers in visible text (CLAUDE.md non-negotiable #5 - a patch must never
///      alter a figure), no on* attributes (DOMPurify strips them silently, so a fix written that
///      way is a no-op), no external script, no JSON/data block, no font block, at most one
///      card-local script;
///  (e) the page still passes ReportEmitNormalizer - counted as a DELTA against the input, so a
///      pre-existing violation (the input already lacks its font/data blocks here) never blocks.
/// </summary>
public static partial class PatchIntegrityGate
{
    public static PatchIntegrityResult Check(string beforeHtml, string afterHtml, IReadOnlyList<int> patchedOrdinals)
    {
        var violations = new List<string>();
        using var before = TileCards.Parse(beforeHtml);
        using var after = TileCards.Parse(afterHtml);
        var cardsBefore = TileCards.CardsOf(before);
        var cardsAfter = TileCards.CardsOf(after);
        var patched = new HashSet<int>(patchedOrdinals);

        // (a)
        if (cardsBefore.Count != cardsAfter.Count)
        {
            violations.Add($"card count changed: {cardsBefore.Count} -> {cardsAfter.Count}");
            return new PatchIntegrityResult(false, violations);
        }
        for (var i = 0; i < cardsBefore.Count; i++)
        {
            if (patched.Contains(i)) continue;
            if (!string.Equals(cardsBefore[i].OuterHtml, cardsAfter[i].OuterHtml, StringComparison.Ordinal))
                violations.Add($"card {i} was not a patch target but changed");
        }

        // (b)
        var idsBefore = IdsOf(before);
        var idsAfterList = after.QuerySelectorAll("[id]").Select(e => e.Id!).ToList();
        var idsAfter = new HashSet<string>(idsAfterList, StringComparer.Ordinal);
        foreach (var id in idsBefore.Where(id => !idsAfter.Contains(id)))
            violations.Add($"id '{id}' disappeared");
        foreach (var dup in idsAfterList.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            violations.Add($"id '{dup}' is no longer unique");

        // (c)
        var pageBlocksBefore = PageLevelBlocks(before);
        var pageBlocksAfter = PageLevelBlocks(after);
        if (!pageBlocksBefore.SequenceEqual(pageBlocksAfter))
            violations.Add($"page-level style/script blocks changed ({pageBlocksBefore.Count} -> {pageBlocksAfter.Count}, or text differs)");

        // (d)
        foreach (var ordinal in patchedOrdinals.Distinct().OrderBy(x => x))
        {
            if (ordinal < 0 || ordinal >= cardsAfter.Count)
            {
                violations.Add($"patched ordinal {ordinal} is out of range");
                continue;
            }
            var b = cardsBefore[ordinal];
            var a = cardsAfter[ordinal];
            if (a.QuerySelector(TileCards.CardSelector) is not null)
                violations.Add($"card {ordinal} now contains a nested card");
            if (TileCards.FingerprintOf(b) != TileCards.FingerprintOf(a))
                violations.Add($"card {ordinal} heading changed");
            var numbersBefore = NumbersOf(VisibleText(b));
            var numbersAfter = NumbersOf(VisibleText(a));
            if (!numbersBefore.SequenceEqual(numbersAfter))
                violations.Add($"card {ordinal} visible numbers changed ({string.Join(' ', numbersBefore)} -> {string.Join(' ', numbersAfter)})");
            foreach (var el in AllElements(a))
            {
                foreach (var attr in el.Attributes)
                {
                    if (attr.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"card {ordinal} uses an inline handler attribute '{attr.Name}' (stripped by the sanitizer, so it would never run)");
                        break;
                    }
                }
            }
            var scripts = a.QuerySelectorAll("script").ToList();
            if (scripts.Any(s => s.HasAttribute("src")))
                violations.Add($"card {ordinal} references an external script");
            if (scripts.Any(s => (s.GetAttribute("type") ?? string.Empty).Contains("json", StringComparison.OrdinalIgnoreCase)))
                violations.Add($"card {ordinal} carries a JSON script block");
            if (scripts.Count(s => !s.HasAttribute("src")) > 1)
                violations.Add($"card {ordinal} has more than one card-local script");
            var outer = a.OuterHtml;
            if (outer.Contains(DimensionDataInjector.ElementId, StringComparison.OrdinalIgnoreCase))
                violations.Add($"card {ordinal} mentions the {DimensionDataInjector.ElementId} block");
            if (outer.Contains("@font-face", StringComparison.OrdinalIgnoreCase))
                violations.Add($"card {ordinal} embeds a font block");
        }

        // (e) - delta only
        var emitBefore = ReportEmitNormalizer.Evaluate(beforeHtml);
        var emitAfter = ReportEmitNormalizer.Evaluate(afterHtml);
        if (!emitAfter.Approved)
        {
            var known = new HashSet<string>(emitBefore.Violations, StringComparer.Ordinal);
            foreach (var v in emitAfter.Violations.Where(v => !known.Contains(v)))
                violations.Add("emit normalizer: " + v);
        }

        return violations.Count == 0 ? PatchIntegrityResult.Pass : new PatchIntegrityResult(false, violations);
    }

    private static HashSet<string> IdsOf(IHtmlDocument document) =>
        new(document.QuerySelectorAll("[id]").Select(e => e.Id!), StringComparer.Ordinal);

    /// <summary>Style and non-JSON script blocks outside every card, in document order, as (tag, text).</summary>
    private static List<string> PageLevelBlocks(IHtmlDocument document) =>
        document.QuerySelectorAll("style, script")
            .Where(e => e.Closest(TileCards.CardSelector) is null)
            .Where(e => !(e.TagName.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase)
                          && (e.GetAttribute("type") ?? string.Empty).Contains("json", StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.TagName.ToUpperInvariant() + ":" + e.TextContent)
            .ToList();

    private static IEnumerable<IElement> AllElements(IElement root) => new[] { root }.Concat(root.QuerySelectorAll("*"));

    /// <summary>Text a reader sees: every text node except those inside script/style elements.</summary>
    public static string VisibleText(IElement element)
    {
        var parts = new List<string>();
        Walk(element);
        return string.Join(" ", parts);

        void Walk(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IText text)
                {
                    parts.Add(text.Text);
                }
                else if (child is IElement el
                         && !el.TagName.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase)
                         && !el.TagName.Equals("STYLE", StringComparison.OrdinalIgnoreCase))
                {
                    Walk(el);
                }
            }
        }
    }

    /// <summary>Sorted multiset of number tokens in visible text ("3,594", "40.7%", "12").</summary>
    public static List<string> NumbersOf(string text) =>
        NumberToken().Matches(text).Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?%?")]
    private static partial Regex NumberToken();
}
