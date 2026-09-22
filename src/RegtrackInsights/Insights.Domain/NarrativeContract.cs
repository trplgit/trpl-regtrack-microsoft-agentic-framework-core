namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-09-20] A row-level citation - names a specific member of a dimension
/// (prompts/v2/03_narrative_analyst.md's root-cause tracing) that the prose cites by name but
/// that is NOT backed by a pre-existing Assertion (e.g. naming a specific performer or branch).
/// Verified the identical way AssertionIdsUsed already is - set-membership against the real,
/// already-fetched dimension_rows for that dimension, never text-parsing. See the design doc:
/// docs/superpowers/specs/2026-09-20-narrative-analyst-agent-design.md Sec.6.
/// </summary>
public sealed record RowRef(string Dimension, string MemberKey);

/// <summary>
/// One block's prose (prompts/03_narrative.md output shape). <see cref="AssertionIdsUsed"/> is
/// the whole mechanism the claim-checker runs on - the narrative agent DECLARES which assertions
/// it drew on, so verification is a set-membership check against a supplied pool, never text
/// parsing. Omitting an id the prose actually relies on is itself a contract violation the
/// prompt calls out ("causes a refusal") - the checker cannot see that from here, but it
/// enforces the half it can: every DECLARED id must be real.
///
/// <paramref name="RowRefsUsed"/> [ADDED 2026-09-20] - optional, defaults to null/empty so every
/// existing caller (the v1 Narrate/Reflect path, Entity/Users, every existing test) is completely
/// unaffected. Only the new v2 analyst agent (prompts/v2/03_narrative_analyst.md, the 5 freehand
/// dimensions) ever populates this - see RowRef's own doc comment.
/// </summary>
public sealed record NarrativeBlockResult(
    string Block, string Prose, IReadOnlyList<string> AssertionIdsUsed, IReadOnlyList<RowRef>? RowRefsUsed = null)
{
    /// <summary>Never null even when the agent/caller left it unset - callers should read this, not the raw property, to avoid a null-check at every call site.</summary>
    public IReadOnlyList<RowRef> RowRefs => RowRefsUsed ?? [];
}

/// <summary>The narrative agent's output for a whole report - one entry per approved composition block.</summary>
public sealed record NarrativeResult(IReadOnlyList<NarrativeBlockResult> Blocks);
