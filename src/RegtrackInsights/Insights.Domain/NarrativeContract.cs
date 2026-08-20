namespace Insights.Domain;

/// <summary>
/// One block's prose (prompts/03_narrative.md output shape). <see cref="AssertionIdsUsed"/> is
/// the whole mechanism the claim-checker runs on - the narrative agent DECLARES which assertions
/// it drew on, so verification is a set-membership check against a supplied pool, never text
/// parsing. Omitting an id the prose actually relies on is itself a contract violation the
/// prompt calls out ("causes a refusal") - the checker cannot see that from here, but it
/// enforces the half it can: every DECLARED id must be real.
/// </summary>
public sealed record NarrativeBlockResult(string Block, string Prose, IReadOnlyList<string> AssertionIdsUsed);

/// <summary>The narrative agent's output for a whole report - one entry per approved composition block.</summary>
public sealed record NarrativeResult(IReadOnlyList<NarrativeBlockResult> Blocks);
