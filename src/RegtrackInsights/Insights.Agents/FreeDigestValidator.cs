using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// The 5 pre-send checks from templates/free_digest_email.md "Validation before send".
/// Any failure discards the LLM body; the caller substitutes the deterministic fallback.
/// Check 4 (numeric diff) is the strongest - the input set is closed, so any unmatched
/// figure is fabricated by definition.
/// </summary>
public static partial class FreeDigestValidator
{
    // The prompt tells the model to say "next 7 days" / "next 30 days" etc - these are
    // window-size labels the prompt supplies, not tenant data, so they are not figures
    // that need to trace to an aggregate.
    private static readonly int[] AllowedWindowLabels = [7, 14, 30];
    private const int MaxWords = 400;

    // No leak list can be complete for free-text world knowledge (the model was never given
    // location/user/department/Act names to diff against), so this is a best-effort net for
    // the where/who/why vocabulary the prompt explicitly forbids - not a closed-set proof
    // like the numeric check below.
    private static readonly string[] LeakMarkers = ["branch", "location", "office", "department", " state of ", " act ", " ltd", "manufactur"];

    public static FreeDigestValidationResult Validate(string body, FreeDigestAggregates aggregates)
    {
        var failures = new List<string>();

        if (body.Contains('%'))
            failures.Add("contains '%' - none of the 15 aggregates is a ratio, so any percentage was computed or invented");

        if (OverdueWord().IsMatch(body))
            failures.Add("contains the word 'overdue' - reserved for the paid tier");

        foreach (var marker in LeakMarkers)
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
                failures.Add($"contains '{marker.Trim()}' - the digest has no location/department/Act data to draw this from");

        var allowedNumbers = AllowedWindowLabels
            .Concat(AggregateValues(aggregates))
            .Select(n => n.ToString())
            .ToHashSet();

        foreach (Match match in NumberToken().Matches(body))
            if (!allowedNumbers.Contains(match.Value))
                failures.Add($"contains the number {match.Value}, which is not one of the 13 aggregates or a window label (7/14/30)");

        var wordCount = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > MaxWords)
            failures.Add($"{wordCount} words exceeds the ~{MaxWords}-word limit");

        return failures.Count == 0 ? FreeDigestValidationResult.Valid : new FreeDigestValidationResult(false, failures);
    }

    private static IEnumerable<int> AggregateValues(FreeDigestAggregates a) =>
    [
        a.TotalActiveObligations, a.BranchesInScope, a.DueNext7, a.CriticalDueNext7, a.ImprisonmentDueNext7,
        a.DueNext14, a.DueNext30, a.CriticalDueNext30, a.ImprisonmentDueNext30, a.LicencesLapsingNext30,
        a.DistinctImprisonmentObligations, a.BranchesWithUpcoming, a.CompletedLast7,
    ];

    [GeneratedRegex(@"\boverdue\b", RegexOptions.IgnoreCase)]
    private static partial Regex OverdueWord();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberToken();
}
