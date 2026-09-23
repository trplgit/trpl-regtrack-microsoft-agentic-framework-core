using System.Globalization;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// The deterministic monthly body used whenever the LLM draft is skipped, truncated, over budget or
/// rejected - the email still goes out (design doc 10.5), same guarantee as the weekly lane.
///
/// Built from the SAME facts the model was given, using each fact's own SQL DisplayLabel, so every
/// number traces straight to the slot proc. It names nothing: a name is only ever placed by the
/// validated LLM path, never by a template that cannot judge whether the sentence around it is true.
///
/// It replaced the weekly templates/digest_fallback.txt, whose numbers came from sql/06's rolling
/// windows - a different question over a different period.
/// </summary>
public static class FreeMonthlyFallbackBody
{
    public static string Build(MonthlyDigestData data)
    {
        var asAt = data.AsOf.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        var month = data.Edition.CurrMonthStart.ToString("MMMM", CultureInfo.InvariantCulture);
        var title = MonthlyDigestCalendar.Title(data.Edition.Slot).ToLowerInvariant();

        var paragraphs = new List<string>
        {
            "Good morning,",
            $"Here is your {title} for {month}, as at {asAt}.",
        };

        /*  Same shape the prompts ask for (shared Rule 14): the headline's section first, then the
            rest, and sections that are only overdue/expired backlog (or context) last and short. */
        static bool BacklogOrContext(MonthlyFact f) => FreeMonthlyDigestPrompt.IsBacklogFact(f) || f.WindowScope == "ctx";

        var sections = data.Facts
            .GroupBy(f => f.Section, StringComparer.Ordinal)
            .Select(g => g.OrderBy(f => f.DisplayOrder).ToList())
            .OrderByDescending(g => g.Any(f => f.IsHeadline))
            .ThenBy(g => g.All(BacklogOrContext))
            .ThenBy(g => g[0].DisplayOrder);

        foreach (var section in sections)
        {
            /*  The first fact of a section is its base ("812 obligations fell due last month");
                the "of those ..." labels after it only read correctly beneath it, so the base is
                always kept. After that, only non-zero facts that matter (tier 1-3) - a fallback is
                a summary, not an inventory - and at most TWO backlog (stock) facts per section, so
                the overdue backlog stays a line of context, never the body of the email.        */
            var backlogShown = 0;
            var shown = new List<MonthlyFact>();
            for (var i = 0; i < section.Count; i++)
            {
                var f = section[i];
                var matters = f.FactValue > 0 && f.SeverityTier <= 3;
                var isBacklog = FreeMonthlyDigestPrompt.IsBacklogFact(f);

                if (i == 0 || f.IsHeadline)
                    shown.Add(f);
                else if (matters && (!isBacklog || backlogShown < 2))
                {
                    shown.Add(f);
                    if (isBacklog)
                        backlogShown++;
                }
            }

            var sentences = shown.Select(f => f.IsHeadline
                ? $"**{f.FactValue.ToString(CultureInfo.InvariantCulture)}** {f.DisplayLabel}."
                : $"{f.FactValue.ToString(CultureInfo.InvariantCulture)} {f.DisplayLabel}.");

            var paragraph = string.Join(" ", sentences);
            if (shown.Any(f => f.AsAtRequired))
                paragraph += $" These figures are as at {asAt}.";

            paragraphs.Add(paragraph);
        }

        paragraphs.Add(FreeMonthlyClosing.For(data.Edition));
        return string.Join("\n\n", paragraphs);
    }
}
