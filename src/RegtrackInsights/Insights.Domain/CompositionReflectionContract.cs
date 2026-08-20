using System.Text.Json.Serialization;

namespace Insights.Domain;

/// <summary>prompts/02_composition_reflection.md output - "approve cleanly if there is not" a flaw.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReflectionVerdict>))]
public enum ReflectionVerdict
{
    Approve,
    Revise,
}

/// <summary>
/// One defect the reflection critic found, mapped to one of its seven named checks (lede,
/// inverted_reading, false_stars, redundancy, padding, missing_caveat, shape_fit).
/// </summary>
public sealed record CompositionReflectionIssue(string Check, string Block, string Problem, string Fix);

/// <summary>
/// The composition critic's verdict. <see cref="Issues"/> is empty when <see cref="Verdict"/> is
/// Approve - a reflection pass that always finds something is as useless as one that never does
/// (the prompt's own closing line), so an empty-but-Revise or populated-but-Approve result is
/// itself a contract violation worth noticing, not just trusting blindly.
/// </summary>
public sealed record CompositionReflectionResult(ReflectionVerdict Verdict, IReadOnlyList<CompositionReflectionIssue> Issues);
