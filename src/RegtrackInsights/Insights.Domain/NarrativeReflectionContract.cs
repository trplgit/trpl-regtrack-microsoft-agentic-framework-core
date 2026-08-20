namespace Insights.Domain;

/// <summary>
/// One defect the narrative critic found, mapped to one of its ten named checks (inversion,
/// unsupported_inference, causal_language, orphaned_caveat, guard_violation,
/// uncomputed_comparative, invented_severity, number_drift, buried_lede, reassurance_or_alarm).
/// <see cref="Quote"/> is the offending text itself - prompts/04_narrative_reflection.md requires
/// it so the fix is unambiguous, unlike composition reflection's issues which quote no prose.
/// </summary>
public sealed record NarrativeReflectionIssue(string Check, string Block, string Quote, string Problem, string Fix);

/// <summary>
/// The narrative critic's verdict - catches what the deterministic claim-checker structurally
/// cannot: a number that maps to a real assertion but tells a false story (inverted direction, an
/// orphaned caveat, invented severity). Reuses <see cref="ReflectionVerdict"/> from composition
/// reflection - same two-outcome semantics, different issue shape.
/// </summary>
public sealed record NarrativeReflectionResult(ReflectionVerdict Verdict, IReadOnlyList<NarrativeReflectionIssue> Issues);
