namespace Insights.Domain;

/// <summary>
/// One block in a composition plan (prompts/01_composition.md output shape). Emphasis is the
/// composition agent's judgement call, never derived from a computed value - only which
/// findings back it (<see cref="FindingIds"/>) is something the gate can check.
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
    CompositionBlockPlan Hero,
    IReadOnlyList<CompositionBlockPlan> Blocks,
    IReadOnlyList<OmittedBlock> Omitted,
    IReadOnlyList<string> DataQualityToSurface);
