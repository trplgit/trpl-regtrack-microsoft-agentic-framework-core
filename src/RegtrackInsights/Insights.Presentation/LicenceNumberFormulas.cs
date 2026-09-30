using System.Linq;
using Insights.Domain;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-09-30, TRIAL] The exact wording for the Licence dimension's
/// "How this number is worked out" hover-links (NumberFormulaInjector). Kept as its own small
/// table, separate from the injector's mechanics, so a future dimension can supply its own list
/// without touching NumberFormulaInjector itself.
///
/// Eight of these fields (Expiring/Applied/PendingForReview/Rejected/ApplicationRejected/
/// Terminated/NotApplicable/OtherStatus/EndingNext30) have no TENANT-level total anywhere -
/// LicenceControlTotals only carries the Active/Expired pair plus the all-time context figures
/// (see its own doc comment) - so they are summed here from the real per-type rows, the same rows
/// already reconciled by sql/v2/23 (each row's status columns sum to its own TotalLicences).
/// </summary>
public static class LicenceNumberFormulas
{
    public static IReadOnlyList<NumberFormulaInjector.Figure> Build(LicenceControlTotals totals, IReadOnlyList<LicenceRow> rows)
    {
        int Sum(Func<LicenceRow, int> selector) => rows.Sum(selector);

        return
        [
            new("Active licences", totals.TenantActiveLicences.ToString(),
                "Counted from each licence's current recorded status: this is how many have the status 'Active' right now."),
            new("Expired licences", totals.TenantExpiredLicences.ToString(),
                "Counted from each licence's current recorded status: this is how many have the status 'Expired' right now - not worked out from any end date."),
            new("Expired percentage", totals.TenantExpiredPct.ToString("0.0") + "%",
                "The number of Expired licences divided by the number counted in this period, shown as a percentage. Example: "
                    + $"{totals.TenantExpiredLicences} Expired out of {totals.ScopedLicences} counted = ({totals.TenantExpiredLicences} / {totals.ScopedLicences}) x 100 = {totals.TenantExpiredPct:0.0}%."),
            new("Expiring licences", Sum(r => r.Expiring).ToString(),
                "Counted from each licence's current recorded status: this is how many have the status 'Expiring' right now."),
            new("Applied", Sum(r => r.Applied).ToString(),
                "Counted from each licence's current recorded status: this is how many have the status 'Applied' right now."),
            new("Pending for review", Sum(r => r.PendingForReview).ToString(),
                "Counted from each licence's current recorded status: this is how many have the status 'Pending for review' right now."),
            new("Rejected", Sum(r => r.Rejected).ToString(),
                "Counted from each licence's current recorded status: this is how many have the status 'Rejected' right now."),
            new("Application rejected", Sum(r => r.ApplicationRejected).ToString(),
                "Counted from each licence's current recorded status: this is how many have the status 'Application rejected' right now - a separate status from 'Rejected'."),
            new("Terminated", Sum(r => r.Terminated).ToString(),
                "Counted from each licence's current recorded status: this is how many have the status 'Terminated' right now."),
            new("Not applicable", Sum(r => r.NotApplicable).ToString(),
                "Counted from each licence's current recorded status: this is how many have the status 'Not applicable' right now."),
            new("Other status", Sum(r => r.OtherStatus).ToString(),
                "Licences whose current recorded status is Draft, Registered, Registered and Renewal Filed, or Validity Expired - grouped together because each one on its own is rare."),
            new("Ending in next 30 days", Sum(r => r.EndingNext30).ToString(),
                "The number of licences whose end date falls within the next 30 days, counted from today - not from the period you picked."),
            new("Licences counted this period", totals.ScopedLicences.ToString(),
                "The number of licences whose end date falls inside the period you picked, counting every status together."),
            new("Across all your licences", $"{totals.AllActiveLicences} active, {totals.AllExpiredLicences} expired of {totals.AllLicences}",
                "Counted across every licence you have access to, whatever its end date - not limited to the period you picked."),
        ];
    }
}
