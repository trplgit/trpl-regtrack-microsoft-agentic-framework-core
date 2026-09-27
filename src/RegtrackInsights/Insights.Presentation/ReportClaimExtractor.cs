using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-09-27] Deterministic helpers for the testers' reasoning file: what a reader actually
/// sees on the finished report, which numbers it shows, and which of those numbers the reasoning
/// file failed to explain. The explainer model is asked to cover every number; this is the check
/// that it did - any number it skipped is listed for manual checking instead of silently missed.
/// </summary>
public static partial class ReportClaimExtractor
{
    /// <summary>Visible text: scripts (including the injected data block), styles and tags removed, entities decoded.</summary>
    public static string VisibleText(string html)
    {
        var text = ScriptOrStyle().Replace(html, " ");
        text = BlockBreak().Replace(text, "\n");
        text = Tag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = SpacesAndTabs().Replace(text, " ");
        text = BlankLines().Replace(text, "\n");
        return text.Trim();
    }

    /// <summary>
    /// Distinct numbers shown in the text, in order of first appearance. Dates, bare years
    /// (1900-2099) and anything in <paramref name="ignore"/> (digits inside names, id values) are
    /// skipped - they are labels, not claims.
    /// </summary>
    public static IReadOnlyList<string> ExtractNumbers(string text, IReadOnlySet<string>? ignore = null)
    {
        var withoutDates = ClockTime().Replace(DateLike().Replace(text, " "), " ");
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (Match m in NumberToken().Matches(withoutDates))
        {
            var token = m.Value;
            var bare = token.TrimEnd('%').Replace(",", "");
            if (!token.EndsWith('%') && !bare.Contains('.') && bare.Length == 4 && (bare.StartsWith("19") || bare.StartsWith("20")))
                continue; // a year
            if (!token.EndsWith('%') && ignore is not null && ignore.Contains(bare))
                continue;
            if (seen.Add(token))
                result.Add(token);
        }
        return result;
    }

    /// <summary>
    /// Numbers that label things rather than claim anything: every digit run inside a text field of
    /// the rows (a law named "... Act, 1955") and every value of an id field (ActID, UserID ...).
    /// </summary>
    public static IReadOnlySet<string> NumbersToIgnore(string? rowsJson)
    {
        var ignore = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(rowsJson))
            return ignore;

        using var doc = System.Text.Json.JsonDocument.Parse(rowsJson);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            return ignore;

        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;
            foreach (var field in row.EnumerateObject())
            {
                if (field.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    foreach (Match d in DigitRun().Matches(field.Value.GetString() ?? ""))
                        ignore.Add(d.Value);
                }
                else if (field.Value.ValueKind == System.Text.Json.JsonValueKind.Number
                         && (field.Name.EndsWith("ID", StringComparison.Ordinal) || field.Name.EndsWith("Id", StringComparison.Ordinal)))
                {
                    ignore.Add(field.Value.GetRawText());
                }
            }
        }
        return ignore;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitRun();

    /// <summary>Numbers whose VALUE never appears in the markdown (84.8% matches 84.80, 21,751 matches 21751).</summary>
    public static IReadOnlyList<string> FindUnexplained(IReadOnlyList<string> numbers, string markdown)
    {
        var present = new List<decimal>();
        foreach (Match m in NumberToken().Matches(markdown))
            if (TryValue(m.Value, out var v))
                present.Add(v);

        return numbers
            .Where(n => !TryValue(n, out var v) || !present.Any(p => Math.Abs(p - v) < 0.005m))
            .ToList();
    }

    private static bool TryValue(string token, out decimal value) =>
        decimal.TryParse(token.TrimEnd('%').Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<\s*(br\s*/?|/p|/li|/h[1-6]|/div|/tr|/section|/article|/header|/aside)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex SpacesAndTabs();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();

    // "27 Sep 2026", "Sep 27, 2026", "2026-09-28"
    [GeneratedRegex(@"\b\d{1,2}\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{4}\b|\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4}\b|\b\d{4}-\d{2}-\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex DateLike();

    // "09:34", "09:34:12 UTC" - a time of day, not a claim.
    [GeneratedRegex(@"\b\d{1,2}:\d{2}(?::\d{2})?\b")]
    private static partial Regex ClockTime();

    // A number not glued to letters on either side ("12m" and "FY2026" are labels, not claims).
    [GeneratedRegex(@"(?<![\w.,])\d{1,3}(?:,\d{3})+(?:\.\d+)?%?(?![\w])|(?<![\w.,])\d+(?:\.\d+)?%?(?![\w])")]
    private static partial Regex NumberToken();
}
