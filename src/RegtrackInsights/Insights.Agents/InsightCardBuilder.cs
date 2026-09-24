using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Agents;

/// <summary>
/// Builds an <see cref="InsightCard"/> from the week's subject data and the validated card text
/// (ADR-0004, revised 2026-09-24). The card is the ten fields the /insights hub binds and nothing
/// else: type, severity, title, the headline figure and up to two supporting figures, all derived
/// here from procedure values; the model contributed only <paramref name="text"/>.
///
/// <para>Works on the JSON lane's own <see cref="InsightCardInput"/>. The closed sets it carries
/// (bindings, allowed numbers) are the data layer's, so a name or number that reaches the card is
/// exactly what the procedure returned.</para>
/// </summary>
public static partial class InsightCardBuilder
{
    /// <summary>The hub shows the headline figure plus two others; more is noise on a five-second card.</summary>
    public const int MaxSupportingMetrics = 2;

    public static InsightCard Build(InsightCardInput input, int customerId, long userId, DateOnly weekEnding, InsightCardText text)
    {
        var prompt = input.Guardrails;
        var data = input.Data;
        var slot = input.Subject;
        var weekOf = weekEnding.AddDays(-6);
        var facts = data.Facts.OrderBy(f => f.DisplayOrder).ToList();

        var candidateLeads = string.Equals(data.HeadlineSource, "candidate", StringComparison.Ordinal);
        var headlineFinding = candidateLeads ? input.NamedFindings.FirstOrDefault(n => n.Slot == 1) : null;
        var headlineFact = candidateLeads ? null : facts.SingleOrDefault(f => f.IsHeadline);

        if (headlineFinding is null && headlineFact is null)
            throw new InvalidOperationException($"The {slot} data carries no headline fact or finding - refusing to build an insight card.");

        /*  Classification is derived from what leads, never promised in advance. The TYPE and the
            TITLE were already derived on the input, because the model is shown the title and told
            not to echo it - deriving them twice would let the two drift apart.                   */
        var impactClass = headlineFinding is not null
            ? InsightCardRules.ImpactClassForDetector(headlineFinding.Candidate.Detector)
            : headlineFact!.ImpactClass;

        var severityTier = headlineFinding?.Candidate.SeverityTier ?? headlineFact!.SeverityTier;

        var primary = headlineFinding is not null
            ? PrimaryFromFinding(headlineFinding, prompt, impactClass)
            : PrimaryFromFact(headlineFact!, impactClass, slot);

        /*  What the headline figure already said, in the chip vocabulary - so no supporting chip
            repeats it back as if it were a second finding.                                      */
        var headlineChip = headlineFinding is not null
            ? InsightCardRules.MetricNounForDetector(headlineFinding.Candidate.Detector)
            : InsightCardRules.MetricNoun(headlineFact!.FactKey);
        if (headlineChip.Length == 0)
            headlineChip = primary.Unit;

        var supporting = SupportingMetrics(facts, primary, headlineChip, slot);

        return new InsightCard(
            InsightId: InsightCardRules.InsightId(customerId, userId, weekOf),
            Tier: InsightCardRules.Tier,
            Type: input.HeadlineType,
            Severity: InsightCardRules.Severity(severityTier),
            WeekOf: InsightCardRules.Date(weekOf),
            Title: input.Title,
            Headline: text.Headline,
            Narrative: text.Narrative,
            PrimaryMetric: primary,
            SupportingMetrics: supporting);
    }

    /// <summary>
    /// The figures that render beside the headline one as "939 lapses · 345 locations". Each label
    /// is a chip, not a sentence, so the hub can print it straight after its bold numeral.
    ///
    /// <para>Two rules keep the pair meaningful. A chip never repeats what another chip or the
    /// headline figure already said, because "12 with liability · 11 with liability" reads as a
    /// contradiction rather than two facts. And a chip names its unit whenever the chips do not all
    /// count the same thing, because "6 · 144" for six PEOPLE beside 144 OBLIGATIONS is exactly the
    /// members-versus-obligations conflation CLAUDE.md Sec.4a exists to prevent.</para>
    /// </summary>
    private static List<InsightSupportingMetric> SupportingMetrics(
        IReadOnlyList<MonthlyFact> facts, InsightPrimaryMetric primary, string headlineQualifier, MonthlyDigestSlot slot)
    {
        var chosen = new List<(MonthlyFact Fact, string Unit, string Qualifier)>();

        void Collect(IEnumerable<MonthlyFact> candidates)
        {
            foreach (var fact in candidates)
            {
                if (chosen.Count == MaxSupportingMetrics)
                    return;

                var unit = InsightCardRules.UnitForFact(fact.FactKey, slot);
                var noun = InsightCardRules.MetricNoun(fact.FactKey);
                var qualifier = noun.Length > 0 ? noun : unit;

                if (qualifier.Equals(headlineQualifier, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (chosen.Any(c => c.Qualifier.Equals(qualifier, StringComparison.OrdinalIgnoreCase)))
                    continue;

                chosen.Add((fact, unit, qualifier));
            }
        }

        // The material facts first: another problem, worth a chip of its own.
        Collect(facts
            .Where(f => !f.IsHeadline && f.FactValue > 0 && f.SeverityTier <= 3 && f.ImpactClass != "volume" && f.WindowScope != "ctx")
            .OrderBy(f => f.SeverityTier).ThenBy(f => f.DisplayOrder));

        /*  [FOUND LIVE on tenant 23, week 2026-08-24] The Licence card shipped with
            supporting_metrics EMPTY. Its headline was "31 licences with no renewal in progress",
            and every other licence fact reduced to the same qualifier - "lapsed" - so each one was
            dropped as a repeat and nothing was left. An empty array leaves the hub's compact line
            with nothing to print at all, which is worse than a plain chip.

            So when the strict pass finds nothing, the denominators are allowed in. They are the
            figures the narrative is already using ("37 licences", "4 locations"), they are present
            on every subject, and "31 lapsed" beside "37 licences" is exactly the of-how-many the
            reader wants. The no-repeat rule still applies, so this can never echo the headline. */
        if (chosen.Count == 0)
            Collect(facts
                .Where(f => !f.IsHeadline && f.FactValue > 0)
                .OrderBy(f => f.WindowScope == "ctx" ? 1 : 0).ThenBy(f => f.DisplayOrder));

        var nameTheUnit = chosen.Select(c => c.Unit).Append(primary.Unit)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

        return chosen
            .Select(c =>
            {
                var unit = c.Fact.FactValue == 1 ? InsightCardRules.Singular(c.Unit) : c.Unit;
                var label = nameTheUnit && !c.Qualifier.Equals(c.Unit, StringComparison.OrdinalIgnoreCase)
                    ? $"{unit} {c.Qualifier}"
                    : c.Fact.FactValue == 1 && c.Qualifier.Equals(c.Unit, StringComparison.OrdinalIgnoreCase) ? unit : c.Qualifier;

                return new InsightSupportingMetric(label, c.Fact.FactValue, c.Unit);
            })
            .ToList();
    }

    private static InsightPrimaryMetric PrimaryFromFact(MonthlyFact fact, string impactClass, MonthlyDigestSlot slot)
    {
        var direction = InsightCardRules.Direction(impactClass, fact.FactKey);

        return new InsightPrimaryMetric(
            Label: NounPhrase(fact, slot),
            Current: fact.FactValue,
            Target: InsightCardRules.Target(direction, fact.FactKey),
            Unit: InsightCardRules.UnitForFact(fact.FactKey, slot),
            Direction: direction);
    }

    private static InsightPrimaryMetric PrimaryFromFinding(MonthlyNamedFinding finding, FreeMonthlyDigestPrompt prompt, string impactClass)
    {
        var c = finding.Candidate;
        var name = finding.NamePlaceholder is { } p && prompt.Bindings.TryGetValue(p, out var bound) ? bound : $"one {c.EntityKind}";
        var unit = InsightCardRules.UnitForDetector(c.Detector);
        var noun = InsightCardRules.ShortNounForDetector(c.Detector);

        var label = c.EntityKind is "user" or "person"
            ? noun.Length > 0 ? $"{Capitalise(unit)} {noun} held by {name}" : $"{Capitalise(unit)} held by {name}"
            : noun.Length > 0 ? $"{Capitalise(unit)} {noun} at {name}" : $"{Capitalise(unit)} at {name}";

        var direction = InsightCardRules.Direction(impactClass, c.Detector);

        return new InsightPrimaryMetric(
            Label: label,
            Current: c.ItemCount ?? c.ProblemCount,
            Target: InsightCardRules.Target(direction, c.Detector),
            Unit: unit,
            Direction: direction);
    }

    /// <summary>The fact's own label as a noun phrase: "of those are still open" becomes "Obligations still open".</summary>
    private static string NounPhrase(MonthlyFact fact, MonthlyDigestSlot slot)
    {
        var label = Plain(fact.DisplayLabel);
        var unit = InsightCardRules.UnitForFact(fact.FactKey, slot);

        if (label.StartsWith("of those ", StringComparison.OrdinalIgnoreCase))
        {
            // "of those past-due obligations carry ..." already names the noun; only "of those are still open" needs one.
            var rest = label["of those ".Length..];
            var opening = string.Join(' ', rest.Split(' ').Take(3));
            label = opening.Contains(unit, StringComparison.OrdinalIgnoreCase) ? rest : $"{unit} {rest}";
        }
        else if (label.StartsWith("of that standing backlog ", StringComparison.OrdinalIgnoreCase))
            label = $"{unit} in the standing backlog {label["of that standing backlog ".Length..]}";
        else if (label.StartsWith("of ", StringComparison.OrdinalIgnoreCase))
            label = $"{unit} {label}";

        // "locations have obligations overdue" -> "Locations with obligations overdue"; "are"/"is" simply drop.
        label = Regex.Replace(label, @"^(\w+) (have|has) ", "$1 with ");
        label = Regex.Replace(label, @"^(\w+) (are|is|were|fell|fall|carry|make up|expire|expired) ", "$1 ");
        return Capitalise(label);
    }

    private static string Plain(string label) =>
        Regex.Replace(label, @"\bitems?\b", m => m.Value.EndsWith('s') ? "obligations" : "obligation", RegexOptions.IgnoreCase)
            .Replace("your scope", "your organisation", StringComparison.OrdinalIgnoreCase).Trim();

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
