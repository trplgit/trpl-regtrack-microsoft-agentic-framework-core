using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// Replaces the placeholders in a VALIDATED monthly draft with the real names, dates and months
/// fixed by <see cref="FreeMonthlyDigestPrompt.Build"/>.
///
/// Asserts BEFORE substituting and fails closed. FreeDigestEmailRenderer.Substitute replaces an
/// unmatched token with an empty string - it fails open - so a placeholder the prompt did not supply
/// would otherwise leave a sentence with a silent hole where a name should be.
/// </summary>
public static partial class FreeMonthlyPlaceholderBinder
{
    public static string Bind(string body, IReadOnlyDictionary<string, string> bindings)
    {
        var unknown = Placeholder().Matches(body).Select(m => m.Value).Where(p => !bindings.ContainsKey(p)).Distinct().ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"Cannot bind placeholder(s) {string.Join(", ", unknown)} - not supplied for this email.");

        var empty = bindings.Where(b => string.IsNullOrWhiteSpace(b.Value)).Select(b => b.Key).ToList();
        if (empty.Count > 0)
            throw new InvalidOperationException($"Placeholder(s) {string.Join(", ", empty)} would bind to empty text.");

        var bound = Placeholder().Replace(body, m => bindings[m.Value]);

        if (bound.Contains("{{", StringComparison.Ordinal) || bound.Contains("}}", StringComparison.Ordinal))
            throw new InvalidOperationException("Placeholder syntax survived binding - refusing to send a body with a raw token in it.");

        return bound;
    }

    [GeneratedRegex(@"\{\{[A-Z0-9_]+\}\}")]
    private static partial Regex Placeholder();
}

/// <summary>
/// The lines the SYSTEM appends after the body (shared rules: "Do not write a closing line"). Fixed
/// text, never model output - so the "next week" promise is always true to the calendar.
/// </summary>
public static class FreeMonthlyClosing
{
    /*  [2026-09-21] Both lines were a flat label ("Next week: a closer look at your locations." /
        "RegInsights Ultimate shows every location, person and law behind these figures.") and the
        email stopped dead on them. They now read as sentences and say what the reader would GET,
        which is the only honest reason to upgrade: this email names a couple of places, the paid
        product names all of them and lets you follow them over time.                            */
    /*  [2026-09-21] Promises nothing the paid tier does not do today. It previously ended "and
        tracks whether each one is improving" - trend tracking is Phase 2 and Features:Snapshots is
        false, so that was a claim we could not keep. What IS true is the difference in coverage:
        this email names a couple of things, the paid report carries all of them.                */
    public const string UpgradeLine =
        "This briefing names only what stands out. RegInsights Ultimate carries the full picture - "
        + "every location, person and law behind these numbers.";

    /*  [FIXED 2026-09-21] These said "Next Sunday". The digest is GENERATED on Sunday and SENT on
        Monday morning (ADR-0001, FreeDigest:Schedule), so the reader has it in their inbox on a
        Monday and the next one arrives a Monday later. Telling them Sunday was simply wrong.    */
    public static string For(MonthlyDigestEdition edition)
    {
        var next = MonthlyDigestCalendar.Next(edition);
        var nextLine = next.Slot switch
        {
            /*  [2026-09-21] This must not promise what the next email cannot deliver. It said "and
                what has moved since this one" - month-over-month change is not computed anywhere
                (snapshots are Phase 2), so the overview simply reports the new month. Each line
                below states the SUBJECT of the next email and nothing more.                     */
            MonthlyDigestSlot.Overview =>
                $"Next Monday: the {next.CurrMonthStart.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture)} overview, across everything in your scope.",
            MonthlyDigestSlot.Users => "Next Monday: the people behind this work, and where it depends on one person.",
            MonthlyDigestSlot.Location => "Next Monday: your locations, and whether this sits across your sites or in a few of them.",
            MonthlyDigestSlot.Act => "Next Monday: the laws themselves, and which are slipping wherever they apply.",
            MonthlyDigestSlot.Licence => "Next Monday: your licences, what is due to expire and what has lapsed unrenewed.",
            _ => throw new ArgumentOutOfRangeException(nameof(edition), next.Slot, null),
        };

        return $"{nextLine}\n\n{UpgradeLine}";
    }
}
