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
                          in a sentence carrying no name at all.

        [TRAP] "office" MATCHED AS A PLAIN SUBSTRING. "officer" contains "office", and "for the
        responsible officer" is the exact phrase this file's OWN worked example uses (§ prompts/
        06_freetier_digest.md) for the personal-liability figure - so every compliant body using
        that wording was rejected, the same class of self-defeating check the sanctioned-closing
        fix above already had to correct once. Word-boundary markers now match on \bword\b so a
        marker cannot fire on a longer word that merely contains it.

        [TRAP] " limited" HAD THE SAME DISEASE, ONE LEVEL UP. Word-boundary matching alone does
        not save a marker that IS an ordinary English word - "limited" (as in "limited in
        number", "limited liability", "a limited window") appears in entirely legitimate,
        non-leaking prose. Confirmed live: "even if they are limited in number" rejected a
        compliant body outright. " ltd"/" pvt"/" limited" exist to catch a hallucinated company
        NAME suffix ("XYZ Limited", "ABC Pvt Ltd") - and a real suffix like that is always
        capitalised, because it is a proper noun, exactly the same reasoning \bAct\b already uses
        below to exempt the lowercase verb. Case-SENSITIVE, capitalised, word-boundary matching
        catches the real leak and cannot fire on the generic lowercase word.                     */
    private static readonly string[] WordBoundaryLeakMarkers = ["branch", "location", "office", "department"];
    private static readonly string[] CapitalisedSuffixLeakMarkers = ["Ltd", "Pvt", "Limited"];

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

        foreach (var marker in WordBoundaryLeakMarkers)
            if (Regex.IsMatch(scannable, $@"\b{Regex.Escape(marker)}\b", RegexOptions.IgnoreCase))
                failures.Add($"contains '{marker}' outside the sanctioned closing - the digest has no location/department/Act data to draw this from");

        foreach (var marker in CapitalisedSuffixLeakMarkers)
            if (Regex.IsMatch(scannable, $@"\b{Regex.Escape(marker)}\b"))
                failures.Add($"contains '{marker}' outside the sanctioned closing - the digest has no location/department/Act data to draw this from");

        if (NamedAct().IsMatch(scannable))
            failures.Add("names an Act - the digest has no Act data to draw this from");

        var allowedNumbers = AllowedWindowLabels
            .Concat(AggregateValues(aggregates))
            .Select(n => n.ToString())
            .ToHashSet();

        /*  [BUG FOUND LIVE, 2026-09-11, first real prod tenant] \d+ alone splits a thousands-
            separated number at the comma - "2,107" (a real DueNext30 value) matched as TWO
            tokens, "2" and "107", neither of which is 2107, so a real number was rejected as
            two fabricated ones. The prompt gives the model a plain integer (2107, no comma) and
            never asks for comma formatting, but writing "2,107" in prose is ordinary English,
            not an instruction violation - check 5 (rule "state numbers exactly as given") is
            about not inventing a DIFFERENT number, not about punctuation. NumberToken now matches
            a comma-grouped run as ONE token; commas are stripped before the allowed-set lookup so
            "2,107" is compared as "2107", which the real aggregate value actually is.            */
        foreach (Match match in NumberToken().Matches(body))
        {
            var normalized = match.Value.Replace(",", "");
            if (!allowedNumbers.Contains(normalized))
                failures.Add($"contains the number {match.Value}, which is not one of the 13 aggregates or a window label (7/14/30)");
        }

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

    /// <summary>Matches a comma-grouped thousands run ("2,107", "12,345,678") as ONE token before falling back to a plain digit run - see the [BUG FOUND LIVE] note at the call site for why.</summary>
    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+|\d+")]
    private static partial Regex NumberToken();
}
