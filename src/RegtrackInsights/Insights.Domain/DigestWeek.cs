namespace Insights.Domain;

/// <summary>
/// The week a digest belongs to.
///
/// Every digest is keyed on the SUNDAY that closes its week, not on the day it was sent. Tenants
/// are staggered across all seven days (design doc 10.3, `hash(tenant_id) % 7`), so the send day
/// varies per tenant while the week label does not - which is what lets one claim per recipient
/// per week mean the same thing for everybody.
/// </summary>
public static class DigestWeek
{
    /// <summary>
    /// The Sunday closing the week that contains <paramref name="asOf"/>. A Sunday closes its own
    /// week rather than the next one.
    ///
    /// [TRAP] Date-level and week-aligned on purpose: this value is the third part of the claim
    /// key, so two runs on different days of the same week MUST produce the same answer or the
    /// claim stops preventing anything. It is also why the value is computed once inside an
    /// activity and threaded through the orchestration as a string - recomputing it mid-replay
    /// could land on a different week.
    /// </summary>
    public static DateOnly EndingFor(DateTime asOf)
    {
        var date = DateOnly.FromDateTime(asOf.Date);
        var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(daysUntilSunday);
    }
}
