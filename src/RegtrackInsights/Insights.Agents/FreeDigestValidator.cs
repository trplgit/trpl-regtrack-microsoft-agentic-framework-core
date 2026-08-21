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

    /*  [TRAP] THE SANCTIONED CLOSING IS NOT A LEAK.
        Design doc Section 10.7 REQUIRES the email to end on the conversion gap, and both the
        prompt and templates/digest_fallback.txt supply the exact sentence:

            "RegInsights Pro shows which locations, which people, and which laws are driving it."

        That sentence names no location - it names the CATEGORY of thing the paid tier reveals,
        which is the entire sales pitch. Scanning it for the word "location" rejected the one
        sentence the spec mandates, so the model was penalised precisely for following its
        instructions, and every compliant body was discarded.

        Worse, it was INTERMITTENT: bodies that omitted the closing - i.e. the ones violating
        10.7 - sailed through, so validation was actively selecting against the spec.

        The closing is therefore removed before the leak scan runs. Matched loosely so line
        wrapping and the optional "- and what to fix first" tail do not defeat it.            */
    [GeneratedRegex(@"RegInsights\s+Pro\s+shows\s+which\s+locations\s*,\s*which\s+people\s*,\s*and\s+which\s+laws\s+are\s+driving\s+it",
        RegexOptions.IgnoreCase)]
    private static partial Regex SanctionedClosing();

    /*  Spec check 3 bans "a location, user, department or Act NAME" - a hallucinated proper
        noun, not the category word. No closed-set proof is possible here: the model was never
        given names to diff against, unlike the numbers in check 4.

        So this stays a best-effort net, applied AFTER the sanctioned closing is removed. Once
        that sentence is gone the model has no legitimate reason to mention a branch, location or
        department at all - it has no such data - so a surviving mention is genuinely suspect.

        Two markers were dropped as pure false positives:
          " act "       - matches the ordinary verb ("act now"), not a named Act. Replaced with
                          a capitalised \bAct\b, which is how a named statute reads.
          "manufactur"  - an industry word, not a name. It would reject "manufacturing sites"
                          in a sentence carrying no name at all.                                */
    private static readonly string[] LeakMarkers = ["branch", "location", "office", "department", " ltd", " pvt", " limited"];

    public static FreeDigestValidationResult Validate(string body, FreeDigestAggregates aggregates)
    {
        var failures = new List<string>();

        /*  Spec check 1 is "a % ADJACENT TO a completion/closure word", not "any %". The
            previous blanket ban was stricter than the document it cites. Narrowing it is safe
            because check 4 backstops it: a fabricated percentage is also a number absent from
            the aggregate set, so it fails there regardless.                                    */
        if (RatioNearClosureWord().IsMatch(body))
            failures.Add("contains a '%' next to a completion/closure word - a ratio was invented (none of the aggregates is a ratio)");

        if (OverdueWord().IsMatch(body))
            failures.Add("contains the word 'overdue' - reserved for the paid tier");

        var scannable = SanctionedClosing().Replace(body, string.Empty);

        foreach (var marker in LeakMarkers)
            if (scannable.Contains(marker, StringComparison.OrdinalIgnoreCase))
                failures.Add($"contains '{marker.Trim()}' outside the sanctioned closing - the digest has no location/department/Act data to draw this from");

        if (NamedAct().IsMatch(scannable))
            failures.Add("names an Act - the digest has no Act data to draw this from");

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

    /// <summary>A percentage within a short span of a completion/closure word - spec check 1.</summary>
    [GeneratedRegex(@"(\d+\s*%[^.]{0,40}?\b(complet|clos|finish|done|resolv)|\b(complet|clos|finish|done|resolv)[^.]{0,40}?\d+\s*%)",
        RegexOptions.IgnoreCase)]
    private static partial Regex RatioNearClosureWord();

    /// <summary>A capitalised "Act" reads as a named statute; the lowercase verb does not.</summary>
    [GeneratedRegex(@"\bAct\b")]
    private static partial Regex NamedAct();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberToken();
}
