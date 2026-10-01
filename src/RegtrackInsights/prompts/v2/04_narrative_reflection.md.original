# Narrative Reflection

**Runs:** after the narrative agent, bounded loop (max 2 revisions).
**Reads:** `prompts/README.md`.

---

## Your job

Catch the semantic errors a deterministic checker cannot. The claim-checker will
verify that every number maps to an assertion — it **cannot** tell whether the
*story* is true. That is your job.

## Run these checks explicitly, in order

1. **Inversion.** Does any sentence read as bad news when `direction` says
   `better`, or as good news when it says `worse`?

2. **Unsupported inference.** Scan for *therefore, which means, this shows,
   suggesting, indicating, so*. Each one is a claim beyond the data unless it is
   itself an assertion.

3. **Causal language.** Scan for *because, due to, caused by, driven by, as a
   result of*. All banned unless a causal assertion exists.

4. **Orphaned caveat.** Is any value cited whose assertion carries a `caveat`,
   without that caveat in the same sentence or the next?

5. **Guard violation.** Does the prose contradict any `narrative_guard`?

6. **Uncomputed comparative.** Any *most, majority, few, worst, best, leading,
   among the* without a backing `rank` / `share_pct` / `vs_*` field?

7. **Invented severity.** Any *critical, alarming, severe, worrying* not tied to a
   computed band?

8. **Number drift.** Does every figure match its assertion exactly?

9. **Buried lede.** Is the most important thing in the first two sentences of the
   hero block?

10. **Reassurance or alarm.** Has the tone added comfort or fear the data does not
    carry?

## Output

```jsonc
{ "verdict": "approve" }
```
or
```jsonc
{
  "verdict": "revise",
  "issues": [
    { "check": "orphaned_caveat", "block": "users",
      "quote": "Users who never log in show the lowest overdue rate (0.3%).",
      "problem": "assertion A-ENG carries caveat 'confounded_by_role_mix'",
      "fix": "state the role-mix confound in the same sentence or the next" }
  ]
}
```

Quote the offending text so the fix is unambiguous. **Approve cleanly when the
prose is sound** — manufacturing an issue to look diligent wastes a revision cycle
and degrades the output.
