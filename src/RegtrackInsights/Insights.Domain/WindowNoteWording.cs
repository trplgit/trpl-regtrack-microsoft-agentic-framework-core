using System.Globalization;
using System.Text.RegularExpressions;

namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-11-03, FOUND LIVE] Most windowed dimension procs emit a 'window' data_quality note
/// that prints @WindowEnd, which is EXCLUSIVE - the day AFTER the last day counted
/// (ReportPeriod.Resolve: last_30_days on 3 Nov = [5 Oct, 4 Nov)). The model read it as the last
/// day and wrote "through 4 Nov" under a header that correctly said "5 Oct - 3 Nov" (demo tenant
/// 1899, Risk). The numbers were right; only the wording was off.
///
/// Fixed here, once, where the notes are read - not in each proc (deployed on demo, UAT and prod)
/// and not in each of the ~30 render/narrate prompt versions. Every note reaches every LLM step
/// through <c>SqlDimensionRepository</c>, so this one rewrite covers them all.
///
/// The procs do NOT all print the same thing (checked on demo 2026-11-03):
///   Act, Departments, Entity, Internal, Location, Nature, Risk, Users: "between X and @WindowEnd"
///   EvidenceIntegrity, TimelinessFY:                                     "X to @WindowEnd"
///   Event:                                                               "between X and @WindowEnd - 1" (already right)
/// So the rewrite is keyed on the run's REAL exclusive end date, never on the wording: only a
/// date equal to it, directly after " and " / " to ", is replaced by the last day counted. Event's
/// already-correct date never equals it and is left alone. Anything that does not match exactly
/// is left as it was (never guess).
/// </summary>
public static partial class WindowNoteWording
{
    [GeneratedRegex(@"(\d{4}-\d{2}-\d{2})( and | to )(\d{4}-\d{2}-\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex DateRange();

    public static List<DataQualityNote> Normalize(IEnumerable<DataQualityNote> notes, DateTime? windowEndExclusive) =>
        notes.Select(n => Normalize(n, windowEndExclusive)).ToList();

    private static DataQualityNote Normalize(DataQualityNote note, DateTime? windowEndExclusive)
    {
        if (windowEndExclusive is not { } endExclusive
            || !string.Equals(note.Issue, "window", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(note.Detail))
            return note;

        var exclusiveText = endExclusive.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var lastDayText = endExclusive.Date.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var changed = false;
        var detail = DateRange().Replace(note.Detail, m =>
        {
            if (m.Groups[3].Value != exclusiveText
                || string.CompareOrdinal(m.Groups[1].Value, lastDayText) > 0)
                return m.Value;
            changed = true;
            return $"{m.Groups[1].Value}{m.Groups[2].Value}{lastDayText} (both days included)";
        });

        return changed ? note with { Detail = detail } : note;
    }
}
