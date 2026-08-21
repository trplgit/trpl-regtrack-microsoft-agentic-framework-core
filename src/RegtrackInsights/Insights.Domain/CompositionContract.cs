namespace Insights.Domain;

/// <summary>
/// The lead block (prompts/01_composition.md output shape). Deliberately a DIFFERENT shape from
/// <see cref="CompositionBlockPlan"/> - {block, reason} vs {block, emphasis, finding_ids} - the
/// prompt's own worked example draws this distinction:
///   "hero": { "block": "coverage_map", "reason": "F-GHOST-AGG is the highest-severity finding" }
/// Confirmed against a real GPT-5.2 response (2026-08-20): using CompositionBlockPlan for both
/// meant Hero.Emphasis/FindingIds came back empty, because the model correctly never populated
/// fields the hero object doesn't have.
/// </summary>
public sealed record CompositionHero(string Block, string Reason);

/// <summary>
/// One block in a composition plan. Emphasis is the composition agent's judgement call, never
/// derived from a computed value - only which findings back it (<see cref="FindingIds"/>) is
/// something the gate can check.
/// </summary>
public sealed record CompositionBlockPlan(string Block, string Emphasis, IReadOnlyList<string> FindingIds);

/// <summary>A block the composition agent considered and chose to leave out, with why.</summary>
public sealed record OmittedBlock(string Block, string Reason);

/// <summary>
/// The composition agent's plan for one report - which blocks appear, in what order, with what
/// emphasis, and what got left out. Reflection (prompts/02_composition_reflection.md) critiques
/// this before narrative ever runs; nothing here is prose yet.
/// </summary>
public sealed record CompositionPlan(
    CompositionHero Hero,
    IReadOnlyList<CompositionBlockPlan> Blocks,
    IReadOnlyList<OmittedBlock> Omitted,
    IReadOnlyList<string> DataQualityToSurface);
