using System.Text.RegularExpressions;

namespace Insights.Agents;

/// <summary>
/// Puts a repaired draft's paragraphs in period order - the lead stays first, then everything
/// about last month, then this month so far, then the standing position, then what is still to
/// come - so that paragraphs about the same period sit next to each other.
///
/// <para>[OWNER, 2026-09-24, PROD tenant 1008] The Location email gave the size of the standing
/// backlog in its second paragraph, moved to a site's last-month figures, and returned to the
/// backlog in its fifth. The shared rules ask for past, present, future in that order, and the
/// model mostly complies, but "mostly" is a paragraph out of place in one email in five. The
/// order is mechanical, so it is applied here: nothing is added, removed or reworded, only the
/// blank lines between paragraphs move. A stable sort keeps the model's order inside each period,
/// so "the backlog" is still introduced before it is referred to.</para>
///
/// <para>The lead paragraph is pinned: the slot prompts open with the headline, which may be a
/// standing figure, and the validator checks it there.</para>
/// </summary>
public static partial class FreeMonthlyParagraphOrder
{
    public static string Arrange(string body)
    {
        var paragraphs = body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var start = paragraphs.Count > 0 && paragraphs[0].StartsWith("Good morning,", StringComparison.Ordinal) ? 1 : 0;
        start += 1;   // the lead paragraph stays where the model put it

        if (paragraphs.Count - start < 2)
            return string.Join("\n\n", paragraphs);

        var previous = Period.Stock;
        var keyed = new List<(Period Period, int Index, string Text)>();
        for (var i = start; i < paragraphs.Count; i++)
        {
            // A paragraph that names no period is explaining the one before it, and stays with it.
            var period = Classify(paragraphs[i]) ?? previous;
            previous = period;
            keyed.Add((period, i, paragraphs[i]));
        }

        var ordered = paragraphs.Take(start).Concat(keyed.OrderBy(k => k.Period).ThenBy(k => k.Index).Select(k => k.Text));
        return string.Join("\n\n", ordered);
    }

    private enum Period { Past = 1, Present = 2, Stock = 3, Future = 4 }

    /// <summary>
    /// Which period a paragraph is about, from the words the shared rules require every figure
    /// to carry. Most specific first: "so far in {{CURR_MONTH}}" beats a mention of
    /// {{PREV_MONTH}} in the same paragraph, because a paragraph contrasting the two months is
    /// about this one.
    /// </summary>
    internal static string? PeriodOf(string paragraph) => Classify(paragraph)?.ToString();

    private static Period? Classify(string paragraph)
    {
        if (Present().IsMatch(paragraph))
            return Period.Present;
        if (Past().IsMatch(paragraph))
            return Period.Past;
        if (Future().IsMatch(paragraph))
            return Period.Future;
        if (Stock().IsMatch(paragraph))
            return Period.Stock;
        return null;
    }

    [GeneratedRegex(@"\bso far\b|since \{\{CURR_MONTH\}\} began|between the 1st|during \{\{CURR_MONTH\}\}|\bthis month\b", RegexOptions.IgnoreCase)]
    private static partial Regex Present();

    [GeneratedRegex(@"\{\{PREV_MONTH\}\}|\blast month\b", RegexOptions.IgnoreCase)]
    private static partial Regex Past();

    [GeneratedRegex(@"\bfalls? due\b|before \{\{CURR_MONTH\}\} ends|between today and|\bexpir(?:e|es|ing)\b|\bdue before\b|\bstill to come\b|\bnot yet late\b", RegexOptions.IgnoreCase)]
    private static partial Regex Future();

    [GeneratedRegex(@"\bbacklog\b|\bcurrently\b|\boverdue\b|\bexpired\b|\bopen obligations?\b|\brests? with\b|\bassigned\b|\bno renewal\b|\bstanding\b", RegexOptions.IgnoreCase)]
    private static partial Regex Stock();
}
