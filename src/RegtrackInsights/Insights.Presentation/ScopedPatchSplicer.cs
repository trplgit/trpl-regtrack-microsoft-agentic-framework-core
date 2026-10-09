using System.Text.Json;
using System.Text.Json.Serialization;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Insights.Presentation;

/// <summary>What one patch LLM call is given: only the failing cards, with page CSS/JS as read-only context.</summary>
public sealed record ScopedPatchPlan(
    string RequestJson,
    IReadOnlyList<int> TargetOrdinals,
    IReadOnlyList<int> UnpatchableOrdinals,
    IReadOnlyList<TileFinding> TargetFindings);

public sealed record SpliceResult(string Html, string Outcome, IReadOnlyList<int> PatchedOrdinals, IReadOnlyList<string> Rejections);

/// <summary>
/// [ADDED 2026-10-09, FOUND LIVE] Scoped patching. The 4.6-4.8 patch loop sent the WHOLE page
/// (100-170 KB) to an LLM asked to return it "byte-for-byte" with one card fixed; it lost the
/// data block, the font block and a script-addressed container on real reports and never once
/// converged. This class makes the model see - and return - only the card(s) it is fixing:
///
///   BuildPlan: locate each finding's card by ordinal + fingerprint (TileCards), pick at most
///              <see cref="MaxCardsPerCall"/> (Functional first, document order), and build the
///              JSON request {findings, cards, page_styles, page_scripts} under hard size caps.
///   Apply:     parse the model's JSON {cards:[{ordinal,html}], unfixable:[...]}, validate each
///              returned card alone (size, exactly one section.card root), splice it into an
///              AngleSharp DOM in place of the original, serialize. A card that fails is dropped
///              and the original kept. Never throws on model output.
///
/// Page-level scripts and styles are context only; the model may NOT replace them in v1 - that is
/// the same "reproduce large opaque content" failure class that lost the data block.
/// </summary>
public static class ScopedPatchSplicer
{
    public const int MaxCardsPerCall = 4;
    public const int MaxCardsChars = 60_000;
    public const int MaxStylesChars = 40_000;
    public const int MaxScriptsChars = 60_000;
    public const int MaxReturnedCardChars = 60_000;
    public const int ReturnedCardGrowthAllowance = 8_000;

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Null (with <paramref name="skipReason"/> set) when nothing can be sent: no finding maps to a
    /// real card, or the request would exceed the size caps even after trimming scripts.
    /// </summary>
    public static ScopedPatchPlan? BuildPlan(string html, IReadOnlyList<TileFinding> findings, out string? skipReason)
    {
        skipReason = null;
        using var document = TileCards.Parse(html);
        var cards = TileCards.CardsOf(document);

        var located = new List<(int Ordinal, TileFinding Finding)>();
        var unpatchable = new List<int>();
        foreach (var finding in findings)
        {
            var ordinal = TileCards.OrdinalOf(finding);
            if (ordinal is null || ordinal < 0 || ordinal >= cards.Count)
            {
                if (ordinal is { } o) unpatchable.Add(o);
                continue;
            }
            // The browser reported the title with the same fingerprint rule; a mismatch means the
            // static card at this ordinal is not the card the checker exercised (script-built
            // cards, or a page that changed since). Not patchable - never guess.
            if (TileCards.Fingerprint(finding.CardTitle) != TileCards.FingerprintOf(cards[ordinal.Value]))
            {
                unpatchable.Add(ordinal.Value);
                continue;
            }
            located.Add((ordinal.Value, finding));
        }

        var targets = located
            .OrderBy(x => x.Finding.Severity == TileFindingSeverity.Functional ? 0 : 1)
            .ThenBy(x => x.Ordinal)
            .Select(x => x.Ordinal)
            .Distinct()
            .Take(MaxCardsPerCall)
            .OrderBy(x => x)
            .ToList();

        if (targets.Count == 0)
        {
            skipReason = findings.Count == 0 ? "no findings" : "no finding could be matched to a card by ordinal and heading";
            return null;
        }

        var targetFindings = located.Where(x => targets.Contains(x.Ordinal)).Select(x => x.Finding).ToList();
        var cardPayloads = targets.Select(o => new { ordinal = o, title = TileCards.FingerprintOf(cards[o]), html = cards[o].OuterHtml }).ToList();
        var cardsChars = cardPayloads.Sum(c => c.html.Length);
        if (cardsChars > MaxCardsChars)
        {
            skipReason = $"target cards total {cardsChars} chars, cap is {MaxCardsChars}";
            return null;
        }

        var styles = string.Join("\n", document.QuerySelectorAll("style")
            .Where(s => s.Closest(TileCards.CardSelector) is null)
            .Select(s => s.TextContent)
            .Where(t => !t.Contains("@font-face", StringComparison.OrdinalIgnoreCase)));
        if (styles.Length > MaxStylesChars)
        {
            skipReason = $"page styles are {styles.Length} chars, cap is {MaxStylesChars}";
            return null;
        }

        var scripts = document.QuerySelectorAll("script")
            .Where(s => s.Closest(TileCards.CardSelector) is null && !s.HasAttribute("src"))
            .Where(s => !(s.GetAttribute("type") ?? string.Empty).Contains("json", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.TextContent)
            .ToList();
        if (scripts.Sum(s => s.Length) > MaxScriptsChars)
        {
            var mentions = targets.SelectMany(o => Tokens(cards[o])).Distinct(StringComparer.Ordinal).ToList();
            scripts = scripts.Where(s => mentions.Any(m => s.Contains(m, StringComparison.Ordinal))).ToList();
            if (scripts.Sum(s => s.Length) > MaxScriptsChars)
            {
                skipReason = $"page scripts exceed {MaxScriptsChars} chars even after keeping only those that reference the target cards";
                return null;
            }
        }

        var request = new
        {
            findings = targetFindings.Select(f => new
            {
                ordinal = TileCards.OrdinalOf(f),
                interaction = f.Interaction,
                severity = f.Severity.ToString(),
                description = f.TechnicalDescription,
            }),
            cards = cardPayloads,
            page_styles = styles,
            page_scripts = scripts,
        };

        return new ScopedPatchPlan(JsonSerializer.Serialize(request, RequestJsonOptions), targets, unpatchable, targetFindings);
    }

    /// <summary>
    /// Outcomes: "patched" (at least one card replaced), "unchanged" (every returned card equal to
    /// its original, or nothing returned), "rejected" (something was returned but none could be
    /// applied - see Rejections). Never throws on model output; malformed JSON is "rejected".
    /// </summary>
    public static SpliceResult Apply(string html, string responseJson, IReadOnlyList<int> targetOrdinals)
    {
        var rejections = new List<string>();
        PatchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<PatchResponse>(StripFences(responseJson), ResponseJsonOptions);
        }
        catch (JsonException ex)
        {
            return new SpliceResult(html, "rejected", [], [$"response is not valid JSON: {ex.Message}"]);
        }
        if (response?.Cards is not { Count: > 0 })
            return new SpliceResult(html, "unchanged", [], response?.Unfixable?.Select(u => $"model reported card {u.Ordinal} unfixable: {u.Reason}").ToList() ?? []);

        using var document = TileCards.Parse(html);
        var cards = TileCards.CardsOf(document);
        var parser = new HtmlParser();
        var patched = new List<int>();
        var targets = new HashSet<int>(targetOrdinals);

        foreach (var returned in response.Cards)
        {
            if (!targets.Contains(returned.Ordinal))
            {
                rejections.Add($"card {returned.Ordinal} was not a target");
                continue;
            }
            if (returned.Ordinal < 0 || returned.Ordinal >= cards.Count)
            {
                rejections.Add($"card {returned.Ordinal} is out of range");
                continue;
            }
            var original = cards[returned.Ordinal];
            var fragment = returned.Html ?? string.Empty;
            var allowed = Math.Min(MaxReturnedCardChars, Math.Max(original.OuterHtml.Length * 3, original.OuterHtml.Length + ReturnedCardGrowthAllowance));
            if (fragment.Length > allowed)
            {
                rejections.Add($"card {returned.Ordinal} returned {fragment.Length} chars, allowed {allowed}");
                continue;
            }

            var parent = original.ParentElement;
            if (parent is null)
            {
                rejections.Add($"card {returned.Ordinal} has no parent element");
                continue;
            }
            var nodes = parser.ParseFragment(fragment, parent);
            var elements = nodes.OfType<IElement>().ToList();
            var stray = nodes.Where(n => n is not IElement && !string.IsNullOrWhiteSpace(n.TextContent)).Any();
            if (elements.Count != 1 || stray)
            {
                rejections.Add($"card {returned.Ordinal} must be exactly one element, got {elements.Count} element(s){(stray ? " plus text" : string.Empty)}");
                continue;
            }
            var replacement = elements[0];
            if (!replacement.TagName.Equals("SECTION", StringComparison.OrdinalIgnoreCase) || !replacement.ClassList.Contains("card"))
            {
                rejections.Add($"card {returned.Ordinal} root is <{replacement.TagName.ToLowerInvariant()} class=\"{replacement.ClassName}\">, not section.card");
                continue;
            }
            if (string.Equals(replacement.OuterHtml, original.OuterHtml, StringComparison.Ordinal))
                continue; // identical - nothing to apply, not a rejection

            original.Replace(replacement);
            patched.Add(returned.Ordinal);
        }

        if (patched.Count == 0)
            return new SpliceResult(html, rejections.Count > 0 ? "rejected" : "unchanged", [], rejections);

        return new SpliceResult(document.ToHtml(), "patched", patched, rejections);
    }

    private static IEnumerable<string> Tokens(IElement card)
    {
        foreach (var el in new[] { card }.Concat(card.QuerySelectorAll("*")))
        {
            if (!string.IsNullOrWhiteSpace(el.Id)) yield return el.Id!;
            foreach (var cls in el.ClassList) if (cls.Length > 2) yield return cls;
        }
    }

    private static string StripFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```", StringComparison.Ordinal)) trimmed = trimmed[..^3];
        }
        return trimmed.Trim();
    }

    private sealed class PatchResponse
    {
        [JsonPropertyName("cards")] public List<ReturnedCard>? Cards { get; set; }
        [JsonPropertyName("unfixable")] public List<Unfixable>? Unfixable { get; set; }
    }

    private sealed class ReturnedCard
    {
        [JsonPropertyName("ordinal")] public int Ordinal { get; set; }
        [JsonPropertyName("html")] public string? Html { get; set; }
    }

    private sealed class Unfixable
    {
        [JsonPropertyName("ordinal")] public int Ordinal { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }
}
