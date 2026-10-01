SUPERSEDED 2026-09-29: reflection (critic + rewrite) removed from the free digest on the product owner's instruction.

# ADR: LLM reflection for the free monthly digest email and insight card (RegInsights Basic)

**Date:** 2026-09-27. R1 was revised twice the same day after product-owner overrides: **R2**,
then **R3**.

**Status:** Proposed, R3. This is the architect's decision revised to the owner's overrides,
pending owner sign-off on the R3 text. It authorises the build and **UAT** use. **Promotion of
the reflection changes to production** waits on the UAT acceptance criteria in Sec.8.

**Scope:**
- The monthly digest email body (`FreeMonthlyDigestComposer`).
- The weekly insight card (`InsightCardComposer`).
- Both lanes' deterministic fallbacks.
- Not in scope: the weekly lane, the paid tier (one finding is recorded in Sec.4.14), SQL, and
  the send path.

---

## Revision log: what the owner overrode, and where the new text lives

| # | Owner decision (2026-09-27, not up for debate) | Text it replaces | Current text |
|---|---|---|---|
| O1 | **Fix first, delete as last resort.** On `revise`, the writer redrafts once. Critic #2 reviews the redraft. Delete-only is the last resort. At most 2 writer calls and 2 critic calls. | R1: Sec.3 Option C rejection; Sec.4.3 "No redraft, ever"; Sec.4.4 | **Sec.4.11** (email flow), **Sec.4.3** (delete step) |
| O2 | **Card lane is in scope**, with the same fix-first shape. | R1: Sec.4.7 ("out of scope") | **Sec.4.12** |
| O3 | **Fallbacks must be send-quality**, looking and reading like the real emails. | R1: C10; the `FreeMonthlyFallbackBody` rationale | **Sec.4.13** |
| O4 | **Cost per path** as token formulas, with the new worst case. | R1: Sec.5 | **Sec.5** |
| O5 | **File list and migration/test list** updated. | R1: Sec.7, Sec.10 | **Sec.7, Sec.10** |
| O6 | **Drop Shadow mode.** The gates become UAT acceptance criteria. | R1: `Mode = Off/Shadow/Enforce`; the Shadow gates | **Sec.8**. *R2's per-lane switch is itself superseded by O7.* |
| O7 (R3) | **No on/off switch of any kind. Reflection is always on**, a fixed part of the pipeline like the validator. **Rollback is a code revert and redeploy.** **Minimise new appsettings keys**, and justify each one kept. | R2: `FreeDigest:Reflection:{Email,Card}:Enabled`; `Budget:FreeMonthlyReflectionTokenCap`; `Budget:InsightCardReflectionTokenCap`; `Llm:AzureOpenAi:ReflectionReasoningEffort` | **Sec.4.5**: **zero new appsettings keys**. **Sec.7**: branch discipline replaces the switch. |

The owner asked whether Shadow should run the full fix path or only critic #1. That question is
**moot**: with O6 and O7, every UAT run executes the full fix path and ships its output, so fix
quality is measured on real output (gate G9).

A superseded passage is replaced by a one-line **`[SUPERSEDED Rn -> Sec.X]`** banner. Its old
text is in git history.

---

## 0. Decision in one paragraph (R3)

Every validator-approved LLM draft, email or card, is reviewed by a critic before it can ship.
The critic is an LLM that can only **name** sentences attaching a real number to the wrong
thing: the wrong period, the wrong entity, a mismatched population, a contradicted direction,
or an overclaimed scope.

**Fix first.** If the critic flags anything, the writer rewrites **once**. It is given its own
sentences and code-built evidence from the data, never wording to copy. The rewrite must pass
the unchanged validator **and** a second critic review. **A redraft never goes out
unreviewed.**

**Delete as last resort.** If the fix path does not yield a reviewed, valid text, the code
deletes the flagged sentences from a draft that **was** reviewed. It then re-validates, subject
to thresholds. If that fails too, the deterministic fallback ships.

**The fallback is upgraded to send quality.** It leads with the headline and names findings
through fixed sentences keyed on the detector **and its metric**. It groups paragraphs by
period, carries the data-quality caveat, and passes the same validator by construction.

**Always on.** Reflection is part of the pipeline like the validator. There is no switch and
there are **no new appsettings keys**. The critic's token caps are code constants. The critic
reuses the writer's existing LLM client at the deployment's own reasoning effort. Rollback is a
revert and redeploy, and the Sec.8 gate is enforced at **promotion**, not by a flag.

**Hard limits and the tradeoff.** At most **2 writer calls and 2 critic calls** per email or
card. Worst-case spend is about **4x today** per email (Sec.5), and it cannot be turned down
without a redeploy. The expected cost depends on critic #1's revise rate, which UAT measures.
Making numbers code-bound placeholders remains the named structural escalation (Sec.3 Option B,
gate G7).

---

## 1. Disposition of the original proposal (updated for R3)

| # | Proposal | Verdict | Change |
|---|---|---|---|
| 1 | After Validate, before Bind. Input is `UserMessage` plus the placeholdered body. No orchestrator bump. Any new output field nullable. | **Confirmed, amended** | The body is sent as a sentence-indexed list. There is **no new output field**: every call's tokens fold into the existing totals. `Source` stays `Llm`/`Fallback` (C1). |
| 2 | Remove sentences only; no redraft. | **Superseded by O1** | Delete-only is the **last resort** inside the fix path (Sec.4.11). The Sec.4.3 mechanics stand. |
| 3 | Fallback on any failure; never ship the unreflected draft. | **Confirmed, extended** | This applies to both critic rounds. The no-throw rule also covers the redraft writer (Sec.4.11.4). |
| 4 | A Mode switch, a token-cap key, a client choice. | **Superseded by O6/O7** | **No switch. No new appsettings keys.** The caps are code constants, and the critic reuses the existing client (Sec.4.5). |
| 5 | The `06r` prompt. | **Confirmed, amended** | Issues carry code-validated `evidence` (Sec.4.2). The card gets its own `07r` (Sec.4.12). |
| 6 | Card lane. | **Superseded by O2** | In scope (Sec.4.12). |

---

## 2. Context

### 2.1 The pipeline as built (verified in code)

`FreeMonthlyDigestComposer.ComposeWithDiagnosticsAsync`
(`src/RegtrackInsights/Insights.Worker/FreeMonthlyDigest.cs`) runs:

- the slot proc;
- `FreeMonthlyDigestPrompt.Build`;
- `FreeMonthlyDigestWriter.WriteAsync`, one `IClaudeClient` call with a post-hoc cap;
- `FreeMonthlyDraftNormalizer.Normalize`;
- `FreeMonthlyDraftRepair.Apply`;
- `FreeMonthlyParagraphOrder.Arrange`;
- `FreeMonthlyDigestValidator.Validate`;
- then either `Bind` plus the closing lines, or `FreeMonthlyFallbackBody.Build`.

`MaxDraftAttempts = 1` is a measured decision, and a validator-rejection redraft
(`FreeMonthlyRedraft.Message`) exists behind it.

Callers:
- `ComposeDigestActivity`, scheduled with `ScheduleWithRetry` (3 attempts) by `FreeDigestGenerateOrchestrator`;
- `FreeMonthlyPreviewWorker`;
- the tests.

The card lane runs:
- `InsightCardComposer.ComposeFromDataAsync`;
- `InsightCardInput.Build`, a separate model input in snake_case with `period`, `figures` and at most 5 facts;
- `InsightCardWriter.WriteAsync`, one call on a separate low-effort client, validated with `ProseProblems`, the headline marker and a 2-3 sentence count;
- then either `InsightCardBuilder.Build` or `InsightCardFallback`.

Its callers are `ComposeInsightJsonActivity` (with `ScheduleWithRetry`, in
`FreeDigestInsightJsonOrchestrator`, which is inert unless `FreeDigest:InsightApi:Enabled` is
on) and `InsightJsonPreviewWorker`. That preview worker supports slot-data replay
(`FreeDigest:InsightPreview:ReplayDir`) but has **no recorded-draft path**: the card writer is
always built from the live card client.

### 2.2 What the deterministic layer already guarantees

- a closed number set and a closed percentage set;
- number words and decimals handled;
- names only as placeholders;
- banned causal and alarm vocabulary;
- evidence for every consequence sentence;
- part no larger than its whole in "N of M";
- regex-listed scope-wide phrases backed by a fact value.

**An invented number or name cannot reach a reader.**

### 2.3 The gap

The validator's own `[FOUND LIVE]` notes show the defects still reaching drafts are **true
numbers attached to the wrong thing**: wrong period, one finding's count given to another,
mismatched part/whole populations, a direction that contradicts `signals`, and a scope-wide
claim in wording the regex does not list. The card has the same class. Its `Figures` note
records "81% of the overdue work" being written for a site where 81% of **its own** overdue
work carried liability.

### 2.4 Constraints found in the code (hard, not preferences)

- **C1. `Source` stays `Llm` or `Fallback`.**
  - `sql/29_freetier_digest_artifact.sql` `THROW`s 51215 on any other value, and the column is `VARCHAR(10)`.
  - The card's `InsightCardText.Source` stays `llm`/`fallback`.
  - No SQL change.
- **C2. An exception escaping a composer re-runs every billed call before it.** Both activities
  use `ScheduleWithRetry` (3 attempts). The no-throw rule covers every call after the first
  (Sec.4.11.4).
- **C3. `IClaudeClient` is registered once**, as the email writer's live client. In the preview
  `DraftsDir` mode, only the writer is constructed with `RecordedDraftClient`, while the DI
  singleton stays live. **R3 reuses that singleton for both critics** (Sec.4.5).
- **C4. `IClaudeClient` has no JSON mode.** JSON is parsed tolerantly, the way
  `InsightCardWriter.Parse` does it.
- **C5. On the reasoning deployment every cap is a post-hoc discard, not a spend limit.** The
  request is governed by `Llm:AzureOpenAi:MaxOutputTokens` (8000). A cap only decides whether an
  already-paid response is used, **so raising a cap costs nothing**.
- **C6. Startup posture.** Every required key stops the worker at startup when it is absent. So
  **any new required key is a deploy-ordering hazard**: every environment's appsettings must be
  updated with the build that reads it. R3 adds none.
- **C7. The email `RecordedDraftClient` answers *every* call with the first recorded draft for
  the slot.**
  - It ignores tenant and scope group.
  - It would also answer the **redraft** call with the original draft.
  - It keys on the email message's `slot` field and on email debug sections, so it cannot serve
    the card at all.
  - The evaluation harness therefore needs recorded-first, live-after clients for **both**
    lanes (Sec.7).
- **C8. CLAUDE.md, Never: "Let an LLM ... validate its own output."** The critic can only veto.
  The deterministic validator gates every text that ships (Sec.6).
- **C9. A latent anaphora defect in `FreeMonthlyDraftRepair.Apply` (production code today).**
  Deleting an unsupported base sentence leaves its "Of those..." follower pointing at the wrong
  base. The Sec.4.3 delete step closes this for its own path.
- **C10. The fallback carried no `not_assessable` caveat.** **Closed by Sec.4.13.**
- **C11. Today's email fallback cannot pass the validator.**
  - It is built after binding: real month names and dates mid-sentence fail the name check,
    and date digits fail the number check.
  - It reads all facts, not the `ForTheModel` set the closed number set is built from.
- **C12. Today's card fallback breaks its own rules.**
  - It repeats the headline as the narrative when no support fact exists.
  - It produces a one-sentence narrative when one exists, below `MinNarrativeSentences = 2`.
  - It prints a raw detector name in its final arm.
  - It asserts `NumbersVerified: true` unchecked.
- **C13. A detector name alone does not fix what a candidate's numbers mean.** The authority is
  the candidate's `Metric` (the `InsightCardInput.Figures` switch).
- **C14 (R3). With no switch, a build containing reflection runs it wherever it is deployed.**
  The Sec.8 gate can only be enforced at **merge and promotion** (Sec.7).

---

## 3. Options considered

**A. Keep patching the validator after each live find.** Rejected as the only answer. It
remains the follow-up for any signature the critic surfaces repeatedly (Sec.4.9).

**B. Numbers become code-bound placeholders.** This makes misattribution a deterministic check.
**Deferred, as the named escalation (G7).**

**C. Critic plus redraft.** **[SUPERSEDED R2 -> Sec.4.11]** The owner chose fix-first. The
unreviewed-redraft objection is resolved by a mandatory critic #2, and spend is bounded at
2 writer + 2 critic calls.

**D. Delete-only, veto-only critic.** Kept, as the **last resort** inside the fix path.

**E. Deterministic set-logic checks.** Kept as a companion (Sec.4.9).

**F (R3). A runtime switch versus always-on.** The owner decided always-on. The cost is that
there is no fast lever: rollback is a redeploy, and spend cannot be dialled down without one.
That is mitigated by revertable sequencing and by gating promotion (Sec.7, C14).

---

## 4. Decision detail

### 4.1 Placement and data flow

- **Where it runs.** Critic #1 runs inside the composer on the first draft that passes
  `Validate`, before `Bind`. The full flow is in Sec.4.11.
- **What the critic receives.** `prompt.UserMessage` **verbatim**, plus the body **after
  Normalize, Repair and Arrange**, still placeholdered.
  - The body is sent as a JSON list of sentences with stable ids (`"2.1"` = paragraph 2,
    sentence 1), with `**` stripped in the copy sent.
  - The greeting is not sent and cannot be cited.
  - No real names leave the process on a critic call.
- **No orchestrator version bump.** The activity sequence and output records are unchanged.
- **No new output field.**
  - Every call's tokens (up to 4 per email) fold into `ComposeDigestOutput.InputTokens` /
    `OutputTokens`, and the card's into `ComposeInsightJsonOutput`.
  - The per-call split goes to logs, metrics and the preview diagnostics (Sec.4.10).
  - `LlmCalls` in both orchestration outputs keeps meaning "scope groups that reached the LLM".
    Its per-call count is no longer 1, and that is documented here.
- **`Source` stays `Llm`/`Fallback` (C1).** Which path produced the text is a closed-set path
  label (Sec.4.10). An email that falls back carries a `Reason` prefix (`reflection: ...`).

### 4.2 Output contract

```jsonc
{ "verdict": "approve", "issues": [] }
// or
{ "verdict": "revise",
  "issues": [
    { "sentence_id": "3.2",
      "check": "period_misattribution",
      "quote": "41 are still open from {{CURR_MONTH}}",
      "evidence": ["lm_still_open"],
      "problem": "41 is lm_still_open, WindowScope prev; the sentence places it in the current month" } ] }
```

- `check` is one of the five names in Sec.4.6. Anything else means the whole response is
  non-conforming (non-negotiable 2).
- **`evidence`** is a non-empty list of keys from the **citable set**.
  - The citable set is enumerated **from the serialized input the critic was actually sent**,
    by one function per lane. It is not a hand-written list.
  - **Email:** every `FactKey` value in `facts`; every placeholder in `named_findings`,
    `examples` and the period tokens; every leaf name under `signals` and `scope`; every
    `not_assessable` `Code`.
  - **Card:** every `fact_key` in `facts` and `scope`; every placeholder; every leaf name under
    `signals`; the `headline.key`.
  - An evidence key outside the set makes the response non-conforming. Building the set from
    the input means that a legitimate citation, such as `scope.obligations_tracked` in a
    `scope_overclaim` issue, can never be refused by mistake.
  - The same enumeration feeds the prompt drift tests (Sec.4.6, 4.12).
- **`problem` is for logs and human review only. It is never sent to the writer.**
  - The owner's flow lists (sentence, check, problem) as what the writer receives. This design
    narrows that deliberately.
  - `problem` is free LLM text, the one field that can carry suggested replacement wording.
  - The code-checked `evidence` gives the writer the same information straight from the data.
- Non-conforming responses: `approve` with issues, `revise` with none, a missing or unknown
  `sentence_id`, or the greeting's id.

### 4.3 The delete-only step (the last resort)

This is deterministic and lives in `Insights.Agents` beside `FreeMonthlyDraftRepair`.

1. **Locate.**
   - The `sentence_id` must exist.
   - The `quote` must be a normalised substring of that sentence: `**` stripped, whitespace
     collapsed, quotes straightened.
   - Otherwise the issue is *unlocatable*.
   - Ids and deletion use the same splitter over the same string: `SplitSentences` becomes
     `internal`.
2. **Delete** the cited sentences.
3. **Cascade** within the paragraph to followers that depend on a deleted sentence:
   - they open with a back-reference (*Of those / These / They / It / Its / This / That /
     Both / The rest ...*), or
   - they carry an antecedent short form (*that liability*).
   Stop at the first independent sentence.
4. **Orphan rule** (reused from Repair): a paragraph left with no digit and no `{{NAME_n}}` /
   `{{EG_n}}` is dropped.
5. **Re-validate** with the unchanged validator.

**The delete step is rejected, and the text falls back, when any of these holds:**

- an issue is unlocatable;
- **more than 2** sentences are cited;
- **under 75%** of the words are retained;
- re-validation fails;
- the headline is lost: `prompt.HeadlineMarker` appears nowhere in the body. The check is
  skipped when the marker is null.

**These thresholds apply only at the delete step and never block the redraft.** Critic #1
citing 5 sentences still goes to the redraft.

The thresholds are code constants, set from the UAT evaluation before promotion (Sec.7).

~~No redraft, ever.~~ **[SUPERSEDED R2 -> Sec.4.11]**

### 4.4 Failure handling

**[SUPERSEDED R2 -> Sec.4.11.3]**

### 4.5 Configuration, client and model (R3: zero new appsettings keys)

**Always on.** The critic is a **required** constructor dependency of both composers: no
optional parameter and no null path.

**Every configuration key considered, and the decision on each:**

| Candidate key | Decision | Justification |
|---|---|---|
| `FreeDigest:Reflection:Mode` / `...:{Email,Card}:Enabled` | **Not added** (owner, O7) | Reflection is always on. Rollback is a revert and redeploy. |
| `Budget:FreeMonthlyReflectionTokenCap` | **Not added: a code constant**, `EmailCriticTokenCap = 14_000` | The writer caps are required keys because they were framed as a spend control that environments may set differently. On this deployment a cap is a post-hoc discard, not a spend control (C5), and it can only ever be *raised* above its code floor. No environment has a reason to differ, and the class's own rule applies: "a value that is identical in every environment does not belong in per-environment config" (`FreeMonthlySettings`, [MOVED OUT OF CONFIG 2026-09-22]). A new required key would also stop every environment's worker until its appsettings is updated (C6). The value 14,000 is the top of the Sec.5 estimate (7,500 in + 3,000 out) divided by 0.8, so it meets G4 by construction. Raising it costs nothing (C5). |
| `Budget:InsightCardReflectionTokenCap` | **Not added: a code constant**, `CardCriticTokenCap = 7_000` | Same reasoning: (3,300 + 2,000) / 0.8. |
| `Llm:AzureOpenAi:ReflectionReasoningEffort` | **Not added.** The critic **inherits the deployment's effort** by reusing the existing client | If UAT shows `low` suffices (G2/G9), the change is a code constant passed to `BuildChatClientFactory`'s existing override parameter. That is the card-writer mechanism, with a constant in place of a key. |
| `FreeDigest:InsightPreview:DraftsDir` | **Added, preview-only.** A developer command-line option, **never in any appsettings file** | This is the card equivalent of the email preview's existing `FreeDigest:Preview:DraftsDir`. It is optional with no default, read only when the card preview runs, and has **no effect on the worker's startup**. Without it, seeded card defects cannot be replayed (C7, Sec.7). |

**Result: zero new appsettings keys, and no new startup failure caused by configuration.** The
one new option is a preview-only command-line flag.

**Related code-constant change (writer floors, no key).**
- Writer #2 sends `W_in + D + I` (Sec.5). On the non-Overview slots its top estimate (about
  9,100 in + 2,500 out, roughly 11,600) exceeds 80% of today's 14,000 floor.
- So `FreeMonthlySettings.MinimumTokenCapFor` for those slots rises from **14,000 to 15,000**.
  The Overview's 16,000 already gives 20% headroom over its roughly 12,100.
- The floor overrides the configured value upward (`Math.Max`), so **no appsettings change**
  follows, and a larger cap costs nothing (C5).

**The one new startup check** is the existence of the two new prompt files (`06r`, `07r`), added
to the existing prompt existence check. Both ship through the `Content Include="prompts\**\*.md"`
glob, so this fails only on a broken build, never on an environment's configuration.

**Client.** Both critics (email and card) resolve the **existing `IClaudeClient` singleton**.
That is the email writer's live client: the same deployment, credentials and reasoning effort
(currently `medium`).
- There is no second registration and no new client construction.
- In the preview `DraftsDir` modes only the writers are swapped for recorded clients. The
  singleton stays live, so a critic is never handed a recorded draft.
- Medium is the right starting effort: the writer's history shows low effort "transcribed
  facts", while medium "weighs the signals block", and weighing is the critic's job.
- The card **writer** keeps its own low-effort client.
- The critic is not the paid tier's `sol` agents: those are a different resource with a
  different cost shape, and the free lane must not contend with paid traffic.

**Existing caps on the redraft.** The writers' existing caps (`Budget:FreeMonthlyTokenCap:{slot}`,
`Budget:InsightJsonTokenCap`) apply **per writer call**, so to the redraft too. UAT confirms
the fit (G4).

### 4.6 The email critic prompt (`06r`)

- File: `src/RegtrackInsights/prompts/06r_freetier_monthly_reflection.md`. Write-once. Its
  existence is checked at startup, and it is listed in `prompts/README.md`.
- **Five checks, a closed set shared with the card critic:**
  - `period_misattribution`
  - `entity_misattribution`
  - `population_mismatch`
  - `direction_contradiction`
  - `scope_overclaim`
- **Excluded:**
  - whether a value exists (the validator's job);
  - style, tone, length, banned words, emphasis, repetition, lead order and naming coverage;
  - **omissions of any kind**. Caveats are handled deterministically (Sec.4.8, Sec.4.13).
- **Glossary**, derived from shared rules Sec.2 and `FactLabels`. It must state:
  - `DisplayLabel` is authoritative for what a figure counts;
  - "of those" labels nest under the fact above them;
  - `BaseCount` is the organisation's whole overdue total under `overdue_concentration`;
  - `lic_lapsed_*` figures sit inside `lic_expired_total`;
  - small-base findings arrive with their percentages absent;
  - grammatical numbers are not attributions: 1 of N, the named count, and the band literals;
  - `obligations_in_scope` and schedule-level counts are different units.
- **The citable evidence set, described exactly as Sec.4.2 enumerates it.**
- **No closed lists** of slots, fact keys or detectors. An unknown `WindowScope` means "do not
  judge period for that fact".
- "Approve cleanly when the prose is sound."
- **Drift guard:** a unit test fails if any top-level `UserMessage` key, or any citable-set
  category from the Sec.4.2 enumeration, is not described in `06r`. The owner of the shared
  rules owns `06r`.

### 4.7 Card lane

**[SUPERSEDED R2 -> Sec.4.12]**

### 4.8 Caveat omission

- **LLM paths:** a validator **advisory** (logged, counted) when a body states a figure bounded
  by `not_assessable` without stating its `ItemCount`.
- **Fallback:** the upgraded fallback always carries the caveat (Sec.4.13), which closes C10.
- Promoting the advisory to a rejection is a follow-up decision.

### 4.9 Companion deterministic check

- **Name-to-count binding** is pure set logic: a sentence with exactly one `{{NAME_i}}` that
  carries a number found only among **another** finding's counts, and in no fact.
- It runs as an **advisory** next to critic #1, to measure its precision and the critic's
  marginal value.
- It is promoted to `SentenceProblems` (deleting) on its own precision (G8).
- Where it reaches recall, that class leaves `06r`.

### 4.10 Observability

All tag values below are closed sets.

- `insights.digest.reflection_path_total{lane, path}`. `lane` is `email` or `card`. `path` is
  one of:
  - `approved_first`
  - `fixed_on_redraft`
  - `redraft_trimmed`
  - `original_trimmed`
  - `fallback_validator` (today's case)
  - `fallback_writer_skipped`
  - `fallback_critic1_failed`
  - `fallback_trim_rejected`
  - `floor_legacy`
- `insights.digest.reflection_issues_total{lane, round, check}`.
- `insights.digest.reflection_call_failed_total{lane, call}`. `call` is `critic1`, `writer2` or
  `critic2`.
- `insights.digest.fallback_floor_total{lane}`. Any non-zero value is an error.
- **Log line per email or card:**
  - the path;
  - tokens per call;
  - each round's issues (`sentence_id`, `check`, `quote`, `evidence`, `problem`);
  - the redraft message as sent;
  - deletions, including the cascade.
- **Preview files (both lanes):**
  - the email `.debug.txt` `== MODEL RAW DRAFT ==` section stays **writer #1's** raw draft;
  - the card preview's debug output gains an equivalent, parseable **writer #1 raw JSON**
    section;
  - both gain a `== REFLECTION ==` section with both rounds, writer #2's input and output, and
    the path;
  - `tokens.csv` keeps **writer #1 only**, so existing rows stay comparable;
  - every call's tokens, plus the path, go to `reflection.csv`.

### 4.11 Email fix-first flow (owner decision O1)

#### 4.11.1 Budget

`MaxWriterCalls = 2` and `MaxCriticCalls = 2` per email are code constants and hard ceilings.

The existing validator-rejection loop (`MaxDraftAttempts`, 1 today) draws from **the same
writer budget**. If it is ever raised and the writer's first draft is rejected by the
validator, the second draft uses the budget. Critic #1's `revise` then goes straight to the
delete step on that draft. The two loops can never compound past 2 writer calls.

#### 4.11.2 States

- **Writer #1**, then Normalize, Repair and Arrange, then `Validate`, giving **draft A**.
  - If writer #1 is skipped, truncated or over its cap: `fallback_writer_skipped`, as today.
  - If `Validate` fails: `fallback_validator`, as today.
- **Critic #1 on A.**
  - `approve`: Bind A and send (`approved_first`).
  - A technical failure (a call error, 429, timeout, truncation, over the cap, unparsable,
    non-conforming, invalid evidence, unlocatable): `fallback_critic1_failed`. A was never
    reviewed, so it cannot ship.
  - `revise`, conforming, every issue locatable: go to writer #2.
- **Writer #2** receives the critic-redraft message (Sec.4.11.5), then Normalize, Repair,
  Arrange and `Validate`, giving **draft B**.
  - Writer #2 fails, is skipped or truncated, or B fails `Validate`: the delete step runs **on
    A** with critic #1's issues.
    - Accepted: send A-minus (`original_trimmed`).
    - Rejected: `fallback_trim_rejected`.
  - B passes `Validate`: go to critic #2.
- **Critic #2 on B.** This is a fresh, full review with the same prompt and **no memory of
  round 1**.
  - `approve`: Bind B and send (`fixed_on_redraft`).
  - `revise`: the delete step runs **on B** with critic #2's issues.
    - Accepted: send B-minus (`redraft_trimmed`).
    - Rejected: `fallback_trim_rejected`, as in the owner's flow.
  - A technical failure: B is unreviewed and cannot ship. The delete step runs **on A** with
    critic #1's issues, giving `original_trimmed` if accepted or `fallback_trim_rejected`
    otherwise. The owner's flow does not cover this case. A-minus is the one fully reviewed
    text available.

**Invariant:** every text that ships either was approved by a critic, or is a critic-reviewed
draft minus exactly the sentences that critic flagged. Every text that ships has passed the
unchanged validator. The redraft never ships unreviewed.

#### 4.11.3 Routing summary

| Condition | Result |
|---|---|
| Writer #1 skipped, or A fails the validator | fallback (as today) |
| Critic #1 approves | send A |
| Critic #1 fails technically | fallback |
| Critic #1 revises; writer #2 fails, or B fails the validator | A-minus, or fallback |
| Critic #2 approves | send B |
| Critic #2 revises | B-minus, or fallback |
| Critic #2 fails technically | A-minus, or fallback |

#### 4.11.4 No-throw

**Only writer #1's** exceptions escape the composer, as today. Nothing has been billed or
reviewed at that point, so the activity retry is the right recovery.

**Critic #1, writer #2 and critic #2 never throw out of the composer.** Their failures are
caught and routed as above; the only exception is the caller's own cancellation.

Otherwise `ScheduleWithRetry` would re-run up to 4 billed calls, 3 times over (C2). The card
composer follows the same rule.

#### 4.11.5 The critic-redraft message

This is **a separate builder** (`FreeMonthlyCriticRedraft`), not `FreeMonthlyRedraft.Message`.

- **Why separate.** `FreeMonthlyRedraft` frames the draft as **rejected** and lists validator
  strings. That would mislead here: the draft passed every rule.
- **What they share.** The closing "change nothing else that was working..." instruction is
  extracted into one constant used by both builders.

**Content, all built by code:**

1. The original `UserMessage`, verbatim.
2. The **reviewed body**: the same repaired, arranged, placeholdered text the critic saw.
3. For each issue:
   - the full cited sentence;
   - a **fixed description of the error class**, from a closed map keyed by `check`. For
     example, `period_misattribution` becomes "a figure is placed in a different period from
     the one its fact covers". These describe the error, never the fix.
   - the **evidence as keys, values and scope fields only**. For example:
     - `lm_still_open = 41 (WindowScope prev)`
     - `{{NAME_2}}: Detector overdue_concentration, Metric share_of_all_overdue_pct, ItemCount 419`

     `DisplayLabel` is **not** restated, because repeating a label next to "fix this" nudges
     the writer to copy label wording, which shared rules Sec.3 forbids.
4. The shared instruction: rewrite the whole email from the same input, correct these
   sentences, and change nothing else.

**What is never sent to the writer:** the critic's `problem` text, and any suggested wording.

### 4.12 Card lane (owner decision O2)

- **Same shape**, adapted to the card: card writer #1, card validation, critic #1, one rewrite,
  card validation, critic #2. The budget is the same (2 writer calls, 2 critic calls), and the
  same no-throw rule applies after writer #1.
- **Units and splitter.** The critic cites `H` (headline), `N1`, `N2` or `N3`. The narrative is
  split with **the card validator's own splitter** (the `SentenceBreak` regex behind
  `InsightCardWriter.SentenceCount`).
- **Delete-only: confirmed as meaningless, with one narrow exception.**
  - The **one** exception: when the **only** unit cited is `N3` of a 3-sentence narrative, `N3`
    is removed. 07b requires sentence 3 to be an optional, self-contained extra finding.
  - The card is then re-validated, and must still have 2 sentences and the headline marker.
  - Otherwise, "still wrong" means the **card fallback**.
- **Routing:**
  - W1 fails validation: card fallback (as today).
  - Q1 approves: ship.
  - Q1 fails technically: card fallback.
  - Q1 revises: W2.
  - W2 fails, or fails validation: the narrow delete on #1 if eligible, otherwise the fallback.
  - Q2 approves: ship #2.
  - Q2 revises: the narrow delete on #2 if eligible, otherwise the fallback.
  - Q2 fails technically: the narrow delete on #1 if eligible, otherwise the fallback.
- **Its own critic prompt: `07r_insight_card_reflection.md`.**
  - The card input is a different shape: snake_case, 5 facts, explicit `period` and `figures`.
  - `06r`'s glossary would mis-describe it.
  - Shared with `06r`: the check enum, the output contract, and evidence validated against the
    lane's own citable set (Sec.4.2).
  - Its existence is checked at startup, and it has a drift test against the card enumeration.
- **Rewrite message:** `InsightCardCriticRedraft`, shaped like Sec.4.11.5. It contains:
  - the card `UserMessage`;
  - the previous `{headline, narrative}` JSON;
  - per issue: the unit text, the fixed class description, and the evidence as keys and values.
  `InsightCardWriter` gains an entry point that takes this message. Validation is unchanged.
- **Cap and client:**
  - critic cap: `CardCriticTokenCap = 7_000`, a code constant;
  - critic client: the shared `IClaudeClient` singleton;
  - the card rewrite uses the card writer's own client and its existing
    `Budget:InsightJsonTokenCap`.
- **Where acceptance runs.** The card orchestrator is inert until
  `FreeDigest:InsightApi:Enabled` is set. Card acceptance runs through
  `InsightJsonPreviewWorker`, which needs the new recorded-first card client (Sec.7).
- **Tokens** fold into `ComposeInsightJsonOutput`, with no contract change.

### 4.13 Send-quality fallback (owner decision O3)

#### 4.13.1 What is true today

- **Design:** both email bodies render through the same `FreeDigestEmailRenderer` /
  `digest.html` shell.
- **Content:** the email fallback is a fact inventory that names nothing and fails the
  validator (C11).
- **Card:** the card fallback names findings but breaks its own rules (C12).

#### 4.13.2 Decision

1. **The shared detector sentences** (`DetectorSentences`, `Insights.Agents`), extracted from
   `InsightCardFallback.FindingSentence`.
   - They emit **placeholder form** (`{{NAME_n}}`, `{{NAME_n_AT}}`, `{{DATE_n}}`,
     `{{PREV_MONTH}}`, `{{AS_AT}}`). Both lanes bind through `FreeMonthlyPlaceholderBinder`,
     and the card strips bold with `PlainText`.
   - **Each arm is keyed on (Detector, Metric)** (C13) and declares its assumed `Metric`.
   - An unknown detector, or an unexpected metric, means that finding is **not named**. The
     raw-detector-name arm is deleted.
   - A test enumerates the known detector set.
2. **The email fallback** (`FreeMonthlyFallbackBody`, rebuilt) is built **over
   `FreeMonthlyDigestPrompt`**: the `ForTheModel` facts, `NamedFindings`, `Examples`,
   `Bindings` and the closed sets. Its structure:
   - `Good morning,`
   - **The lead:** the headline.
     - If a finding leads: that finding's detector sentence.
     - If a fact leads: the fact, emphasised, inside its section base, with "as at {{AS_AT}}"
       where `AsAtRequired`.
   - **Period-grouped paragraphs, in `FreeMonthlyParagraphOrder` order:**
     - {{PREV_MONTH}};
     - this month so far;
     - the standing position, including the backlog and the findings that describe it;
     - what is still to come.
     Each named finding goes in its detector's period. Each example is appended to its pattern
     fact's sentence.
   - **The `not_assessable` caveat**, as a fixed sentence per `BoundsAFigure` code, in the
     paragraph whose figures it bounds. This closes C10.
   - **Labels:** `FactLabels`' reader wording. `FactLabels` changes from `file`-scoped to
     `internal`.
   - **Length:** sentences are chosen by severity, so the body fits `MaxWordsFor(slot)` and is
     at least 25 words. At least one finding is named whenever one is nameable.
   - **Pipeline:** build (placeholders), then **Normalize** (emphasis parity; `**` only), then
     **Validate**, then **Bind**, then the closing lines.
     - **No Repair:** it cleans model prose.
     - **No Arrange:** the body is built in order. A test asserts `Arrange(body) == body`.
3. **The card fallback** is rebuilt on `DetectorSentences`.
   - The narrative is always 2-3 sentences. It is never the headline repeated.
   - It passes `ProseProblems`, the headline-marker check and the sentence count.
   - `NumbersVerified` becomes true because the check actually runs.
   - **Its output changes where it breaks its rules today (C12).** Elsewhere, fixture tests pin
     it as unchanged.
4. **Runtime gate and floor.**
   - The upgraded fallback is validated at runtime too.
   - If it fails, the error is logged, `fallback_floor_total` is counted, and **the legacy
     builder ships as the floor**. The legacy builder is retained unchanged and is not
     selectable.
   - This keeps the 10.5 guarantee, and a failure of the new template is loud.
5. **The rationale is revised.**
   - "A template cannot judge whether the sentence around a name is true" holds for a generic
     template.
   - It does not hold for **fixed per-(detector, metric) sentences filled only with that
     candidate's own values**. Those are true by construction, provided the metric assumption
     is asserted (item 1).
   - The doc comment is rewritten to say exactly that.
6. **CI invariants:**
   - for every slot and every fixture, including the **largest shape** (Overview: 24 facts,
     4 names, 6 examples), the zero-fact, no-finding, single-member and
     `not_assessable`-present cases, and every captured replay fixture:
     - the email fallback passes `Validate` with zero failures, and `Bind` succeeds;
     - the card fallback passes its three checks.
7. **Rollout.** It ships **first**, directly, as its own revertable change, and it can be
   promoted on **G10 alone** (Sec.7).
   - It changes every email and card that falls back today, which is the intent.
   - It lowers the cost of every fallback route the fix path adds.

### 4.14 Finding for the paid tier, recorded and not fixed here

`InsightsReportOrchestrator`'s narrative loop,
`for (i < maxReflectionIterations) { reflect; if approve break; narrate(revision) }`, exits
after the final revision. **The last revision reaches `PublishGate` without any LLM
reflection.**

- It is still deterministically gated, but it is unreflected.
- The fix is to end the loop on a reflection and refuse or fall back on a final `revise`.
- That changes the orchestrator body, so it **requires an orchestration version bump** in its
  own change.

---

## 5. Cost, latency and fallback-rate risk (owner decision O4)

There are no prices in the repo. Below are **token formulas per path**, for the owner's own
cost analysis. With reflection always on, **the `approved_first` path is the floor for every
validator-approved email and card**. There is no way to spend less without a redeploy.

| Symbol | Meaning | Tokens |
|---|---|---|
| `W_in`, `W_out` | email writer #1 | 6,000-7,500 in, 1,000-2,500 out (measured) |
| `D` | the reviewed body re-sent to writer #2 | ~1,000-1,300 |
| `I` | the code-built issue block | ~150-250 per issue; 200-800 typical |
| `W2_in`, `W2_out` | email writer #2 | `W_in + D + I` (about 7,200-9,600) in, about `W_out` out |
| `C_in`, `C_out` | email critic, per call | 4,500-7,500 in; 1,000-3,000 out at medium effort (inherited) |
| `K_in`, `K_out` | card writer #1 | ~3,000-4,000 in (to be measured); about 900 out at low (measured, tenant 1082) |
| `K2_in` | card writer #2 | `K_in + ~150 + I_card (~150-500)` |
| `Q_in`, `Q_out` | card critic, per call | ~3,000-3,300 in; 600-2,000 out (medium, inherited) |

Reasoning tokens bill as output, and output usually prices several times higher than input, so
**output dominates**.

**Email, per scope group per edition:**

| Path | Calls | Input | Output |
|---|---|---|---|
| Today (reference) | W | `W_in` | `W_out` |
| `fallback_writer_skipped`, `fallback_validator` | W | `W_in` | `W_out` |
| `approved_first` (**the new minimum for a validator-approved draft**) | W + C | `W_in + C_in` | `W_out + C_out` |
| `fallback_critic1_failed` | W + C | `W_in + C_in` (or less) | `W_out + C_out` (or less) |
| `original_trimmed` / `fallback_trim_rejected` after writer #2 fails validation | W + C + W2 | `W_in + C_in + W2_in` | `W_out + C_out + W2_out` |
| `fixed_on_redraft`, `redraft_trimmed`, the trim after critic #2 fails **(worst case)** | W + C + W2 + C | `2W_in + 2C_in + D + I` = **~22,000-32,000** | `2W_out + 2C_out` = **~4,000-11,000** |

- **Worst case versus today:** about **3.5-4.3x** input and **4-4.4x** output.
- **Expected per email:** `E = W + C + r1 * (W2 + p2 * C)`, where `r1` is critic #1's revise
  rate and `p2` is the share of redrafts that pass the validator. **Both are measured in UAT
  (Sec.8).** At `r1 = 0.1`, about 1.9-2.2x today; at `r1 = 0.3`, about 2.5-2.9x.
- **Card, per scope group per week:** the worst case is `2K + 2Q`: about **12,400-15,100 in**
  and **3,000-5,800 out**. The expected cost is `K + Q + r1_card * (K2 + p2_card * Q)`.
- **Frequency:**
  - email: 4-5 editions per month times scope groups (tenant 1285 has 6);
  - card: weekly times scope groups, and only once the InsightApi lane is enabled.
- **Budget breakers are unaffected.** `PerRunTokenCeiling` and `PerTenantMonthlyTokenCeiling`
  are enforced only by the paid orchestrator.
- **Per-call caps, sized to meet G4 by construction.** p95 of at most 80% of the cap:

  | Call | Cap | Top estimate |
  |---|---|---|
  | Email critic | 14,000 constant | ~10,500 |
  | Card critic | 7,000 constant | ~5,300 |
  | Email writer #2, Overview | 16,000 floor | ~12,100 |
  | Email writer #2, other slots | 15,000 floor (raised from 14,000) | ~11,600 |

  Raising a cap costs nothing (C5). UAT confirms the fit (G4).
- **Latency.** The worst case is 4 serial reasoning calls per scope group, estimated at
  1-3 minutes per email, less per card. Generation is on Sunday and sending on Monday, so no
  reader sees it.
- **Rate limits.** In the worst case, Sunday tokens per minute grow up to about 4x. UAT cannot
  reproduce production load, so this is a **production watch item** (Sec.7 Step 5). The lever
  is a revert and redeploy.
- **Fallback-rate risk.** Fallback happens only when writer #1 fails (as today), critic #1
  fails technically, or a delete step is rejected. **And a fallback is now send-quality
  (Sec.4.13).**

---

## 6. Consequences

**Positive**

- Misattributions are **corrected**, not only removed.
- Every shipped text is critic-reviewed and passes the unchanged validator. The redraft is
  never unreviewed.
- A fallback reads like the product, which closes C10, C11 and C12.
- **Zero new appsettings keys** and no new configuration-driven startup failures. No
  environment needs an appsettings change for this work.
- No schema change, no durable-contract change, no orchestrator bump.

**Negative, accepted**

- **No fast lever (O7).** Rollback is a revert and redeploy. Spend cannot be turned down, and
  a misbehaving critic cannot be disabled, without one. It is mitigated by revertable
  sequencing and by gating promotion (Sec.7).
- **Every** validator-approved email and card now costs at least `W + C`. The worst case is
  about 4x (Sec.5).
- **Branch freeze.** While Steps 2-3 sit on `staging` awaiting Sec.8, `staging` cannot be
  promoted to `main` wholesale (Sec.7).
- Tuning the critic's effort or caps is a code change, which is the same as the floors and
  `MaxDraftAttempts` today.
- Higher latency, on the Sunday batch only.
- The verdicts are not reproducible run to run. A re-executed activity can produce a different
  email. The writer already has this property.
- A redraft can lose names or words while fixing an attribution. G9 measures this.
- Four prompts now describe inputs (`06` shared, `06r`, `07b`, `07r`). Drift tests cover `06r`
  and `07r`.
- Composers gain a state machine. It is extracted into `FreeMonthlyFixPath` and a card
  counterpart, which are testable against fakes.
- The existing composer tests must supply an approving fake critic, and their expected token
  totals change. `ARejectedDraftIsRedraftedWithItsFailures_AndBothAttemptsAreBilled` is the
  obvious case.

**Compliance with CLAUDE.md**

- *Non-negotiable 1:* the critic decides nothing about values. Rewrites are validated like any
  draft.
- *Never "let an LLM validate its own output"* (C8): the critic only vetoes, and a redraft
  ships only after the **deterministic** validator **and** a second veto check. Nothing ships
  because an LLM approved it alone.
- *Non-negotiable 2:*
  - Unknown `check` values and unknown evidence keys are refused.
  - Every failure routes to a reviewed text or the fallback, with a reason and a metric.
  - A failed new fallback is loud, and ships the legacy floor.
  - A missing prompt file fails startup.

**Findings recorded, fixed separately**

- **C9 (production today):** Repair's anaphora gap.
- **Sec.4.14:** the paid tier ships its last revision unreflected. The fix needs a version bump.

**Unchanged:** the weekly lane, all orchestrators, all SQL, the send path, the artifact store,
and **appsettings in every environment**.

---

## 7. Migration path (owner decisions O5, O6, O7)

Per standing instruction, **the user or team runs every capture, preview, build, test, git and
database step**. There is no SQL, no Shadow phase and no switch.

**Branch discipline replaces the switch (C14).**

- This assumes, unverified, that UAT deploys from `staging` and production from `main`.
- **Step 1 lands on `staging` first and promotes to `main` on G10 alone**, before Steps 2-3
  land. It is independent of reflection, so it is never caught in the freeze below.
- **Steps 2-3 then land on `staging` and stay off `main` until Sec.8 passes.** While they sit
  there, **`staging` cannot be promoted to `main` wholesale**. That is a deliberate freeze for
  the evaluation window. Unrelated production fixes go from `main`, as hotfix branches.
- If the freeze becomes unacceptable, revert the Step 2 and/or Step 3 change from `staging`.
  Each is its own change.
- The alternative is a feature branch deployed to UAT. It is rejected unless a second UAT
  deployment slot exists, because it would displace `staging` from the one UAT environment.
- Each step below is **its own revertable change** (commit or PR).

**Step 1: send-quality fallbacks (Sec.4.13). Independent of reflection.**

- Extract `DetectorSentences`.
- Rebuild `FreeMonthlyFallbackBody` over the prompt.
- Rebuild the card fallback.
- Make `FactLabels` `internal`.
- Keep the legacy builders as the floor.
- Tests:
  - the CI invariants (4.13 item 6);
  - `Arrange(body) == body`;
  - (detector, metric) assertions and the detector enumeration;
  - the card fallback is unchanged on fixtures that pass today, and valid on the C12 shapes;
  - the floor path runs on an injected validator failure.
- Preview on UAT tenants, then promote on G10.

**Step 2: email fix path (Sec.4.1-4.6, 4.8-4.11).**

- Add `06r` and its startup existence check.
- The critic is a required dependency resolving the existing `IClaudeClient` singleton.
- The critic cap is a code constant.
- `MinimumTokenCapFor` for the non-Overview slots rises to 15,000.
- **No appsettings change.**
- Tests:
  - every row of Sec.4.11.3 against fakes;
  - the budget: never more than 2 writer or 2 critic calls, including with
    `MaxDraftAttempts = 2`;
  - no-throw for critic #1, writer #2 and critic #2, while writer #1 still propagates;
  - tokens fold across calls, and `tokens.csv` stays writer #1 only;
  - `Source` is only `Llm`/`Fallback`;
  - the delete applier: mapping, quote check, cascade, orphan, thresholds, headline loss and
    the null marker;
  - strict parsing: an unknown `check`, an evidence key outside the enumerated citable set, an
    inconsistent verdict, bad ids;
  - **a citation of a `scope.*` leaf or a `not_assessable` code is accepted**;
  - the redraft message: no `problem` text, no `DisplayLabel` restatement, and verbatim quotes;
  - startup fails when `06r` is missing;
  - the `06r` drift test against the same enumeration;
  - existing composer tests updated with an approving fake critic.

**Step 3: card fix path (Sec.4.12).**

- Add `07r` and its startup check, with the same patterns.
- Tests: the card routing table, the narrow `N3` delete, the splitter shared with
  `SentenceCount`, no-throw, and the `07r` drift test against the card enumeration.

**Step 4: UAT evaluation (on `staging`).**

1. Capture all 5 slots for the evaluation tenants.
   - Tenants: **1285**, **29**, **5**, **522**, **2480 or 1807**, plus 1490 / 1403 / 1472 /
     1308 where they have UAT data (U2).
   - **Never 1082.**
2. Run both previews once. Each debug output records **writer #1's raw draft or JSON**, which
   is the unseeded set, together with the reflection rounds. No switch is needed to obtain
   unreflected drafts.
3. Seed at least 40 email defects (at least 8 per check class), and at least 20 card defects.
   - Edit writer #1's raw section to inject them.
   - Each seed carries a distinctive substring that Repair leaves alone.
   - Expected ids are read from the replayed `== REFLECTION ==` sentence or unit list.
   - A seed the validator already catches is validator coverage.
4. **Harness changes (C7, preview-only code):**
   - **Email:** in `FreeDigest:Preview:DraftsDir` mode the writer becomes **recorded-first,
     live-after**. A call whose message is exactly the original `UserMessage` gets the
     recorded draft, and the redraft call goes to the live writer.
   - **Card (new):** `InsightJsonPreviewWorker` has no recorded-draft path today. A new
     preview-only recorded-first card client is added, read from the card debug output's
     writer #1 JSON section and selected by the new command-line option
     `FreeDigest:InsightPreview:DraftsDir`. The rewrite call goes to the live card client.
   - Use **one DraftsDir per (tenant, scope group)** in both lanes.
5. Replay. To evaluate `low` effort, a **local, uncommitted** code-constant change is enough;
   no key is needed.
6. A person labels every critic issue and **reviews every redraft critic #2 approved** (G9).
7. Set the Sec.4.3 constants, and the caps if needed, from the data.

**Step 5: promote Steps 2-3 after Sec.8 passes.** A lane may promote on its own gates.

- Merge to `main` and deploy. **There are no appsettings changes to make.**
- **Watch the first 4 production Sundays:**
  - critic and writer #2 call failures, including 429s;
  - the path mix (`fallback_*` versus baseline);
  - `fallback_floor_total`, which must stay 0.
- **The lever is a revert and redeploy of the Step 2 and/or Step 3 change.** Use it if a lane's
  reflection-attributable fallbacks exceed 3%, or its 429 rate exceeds 1%. Step 1 stays.

---

## 8. UAT acceptance criteria, before promotion (owner decisions O6, O7)

All are measured in UAT, per lane unless noted. With no switch, **these gate the merge of Steps
2-3 to `main`**. Step 1 is gated by G10 alone.

| Gate | Measure | Threshold |
|---|---|---|
| **G1 Precision** | Unseeded drafts (email: at least 45 = 9 tenants x 5 slots; card: at least 45) with at least one false-positive issue from critic #1. | **<= 5%**, and **zero** false positives on a headline unit or on the only `{{NAME_n}}` sentence. |
| **G2 Recall** | Seeded defects cited by the correct id, by critic #1. | **>= 80% overall, >= 60% per class.** No seeded defect survives in any shipped text. |
| **G3 Conformance** | Critic responses that parse and conform, **including evidence inside the enumerated citable set**. | **>= 99%** |
| **G4 Operations (UAT)** | Critic and writer #2 call failures over the UAT evaluation. p95 tokens per call against its cap: writer #2 against its floor (16,000 / 15,000), and each critic against its constant (14,000 / 7,000). | **<= 1%** failures. p95 **<= 80%** of cap. The production 429 rate is a Step 5 watch item. |
| **G5 Fallback** | Unseeded runs ending in `fallback_*` other than `fallback_validator` / `fallback_writer_skipped`. | **<= 3 points above** today's fallback rate on the same runs. `floor_legacy` = **0**. |
| **G6 Value (kill rule)** | Human-confirmed true misattributions on **unseeded UAT** drafts, excluding those the Sec.4.9 companion caught. | **>= 1 per 100** to promote. Below that, that lane's change is **not promoted** (and is reverted from `staging`). N is small, so the threshold stays a product call (U3). |
| **G7 Escalation** | G1, G2 or G9 still failing after one prompt revision (`_v2`). | Stop and open the Option B ADR. |
| **G8 Companion** | Precision of the Sec.4.9 advisory. | **>= 98%** before it may delete. |
| **G9 Fix quality** | Every redraft critic #2 approved on the seeded set, reviewed by a person. Critic #2's approval is not independent evidence. | **>= 90%** have: the seeded defect gone; no new misattribution; no named finding lost; word loss **<= 25%**. |
| **G10 Fallback quality** (Step 1 only) | CI invariants pass. A person reads fallbacks for every slot on UAT tenants, and the card fallback for every subject. | **100%** of invariants pass. The owner signs off that fallbacks "look and read like the actual emails". |

---

## 9. Open items (UNRESOLVED; external inputs, not design questions)

- **U1. Currency.** No price sheet for `gpt-5.6-luna` is in the repo. Apply one to the Sec.5
  formulas with the measured `r1`, `p2` and `reflection.csv`.
- **U2. Evaluation tenants.**
  - 29 and 5 have UAT history.
  - Whether 522, 2480/1807, 1490, 1403, 1472 and 1308 have usable UAT data is unverified.
  - If they do not, a data-policy owner decides on captures from the read-only replica.
  - 1082 is excluded regardless.
- **U3. The G6 threshold** is a product and commercial call. It is also statistically weak on a
  UAT-sized sample.
- **U4. Measured sizes** replace the Sec.5 estimates. A p95 above 80% of any cap raises the
  corresponding code constant or floor.
- **U5. Branch-to-environment mapping.** Sec.7 assumes UAT deploys from `staging` and
  production from `main`. If the mapping differs, the branch discipline is re-expressed against
  the real mapping, with the same rule: nothing carrying Steps 2-3 reaches production before
  Sec.8.
- **Non-blocking note:** `Budget:InsightJsonTokenCap` has a code default of 3,000 that its own
  `appsettings` comment says "would fall back on every call", and the card redraft inherits
  this cap. It is not changed here, because O7 minimises configuration changes. Making it a
  floored constant, like the critic caps, is a separate change.

---

## 10. Files this decision touches (owner decisions O5, O7)

**Email lane**

- `src/RegtrackInsights/Insights.Worker/FreeMonthlyDigest.cs`: the composer delegates to the fix path; the critic is a required dependency; the prompt existence check covers `06r` and `07r`; `MinimumTokenCapFor` (non-Overview) rises to 15,000; the diagnostics split. **No new settings keys.**
- `Insights.Worker/` (new) `FreeMonthlyFixPath`: the Sec.4.11 state machine, the budget constants and the critic cap constant.
- `Insights.Agents/` (new): the email critic caller; the citable-set enumeration; the deterministic delete applier; `FreeMonthlyCriticRedraft`.
- `Insights.Agents/FreeMonthlyRedraft.cs`: extract the shared "change nothing else" constant.
- `Insights.Agents/FreeMonthlyDraftRepair.cs`: `SplitSentences` becomes `internal`; the cascade constant is shared (Repair's own adoption is a separate change, C9).
- `Insights.Agents/FreeMonthlyDigestValidator.cs`: the caveat advisory and the name-binding advisory.

**Card lane**

- `Insights.Worker/InsightCardComposer.cs`: the card fix path, or a card counterpart class; the critic is required; no-throw after writer #1.
- `Insights.Agents/InsightCardWriter.cs`: a rewrite entry point; exposure of the `SentenceBreak` splitter.
- `Insights.Agents/` (new): the card critic caller, the card citable-set enumeration, and `InsightCardCriticRedraft`.

**Fallbacks**

- `Insights.Agents/` (new) `DetectorSentences`, extracted from `InsightCardFallback.FindingSentence`.
- `Insights.Agents/FreeMonthlyFallbackBody.cs`: rebuilt over the prompt; legacy kept as the floor; doc-comment rationale revised.
- `Insights.Agents/InsightCardWriter.cs` (`InsightCardFallback`): rebuilt on `DetectorSentences`; the legacy floor kept.
- `Insights.Agents/FreeMonthlyDigestPrompt.cs`: `FactLabels` changes from `file` to `internal`.

**Wiring, observability and preview**

- `Insights.Worker/FreeDigestRegistration.cs`: register both critics against the **existing** `IClaudeClient` singleton; in the card preview's `DraftsDir` mode, construct the card writer with the recorded-first card client. **No new appsettings keys, no new production client.**
- `Insights.Worker/FreeDigestMetrics.cs`: the Sec.4.10 instruments.
- `Insights.Worker/FreeMonthlyPreviewWorker.cs`: the `== REFLECTION ==` section, a writer #1-only `tokens.csv`, and `reflection.csv`.
- `Insights.Worker/InsightJsonPreviewWorker.cs`: a writer #1 raw JSON section, the `== REFLECTION ==` section, `reflection.csv`, and `FreeDigest:InsightPreview:DraftsDir` (command-line only).
- `Insights.Worker/RecordedDraftClient.cs`: recorded-first, live-after for the email redraft call. **A new** recorded-first card client sits beside it. Both are preview-only.

**Prompts**

- `src/RegtrackInsights/prompts/06r_freetier_monthly_reflection.md` and `07r_insight_card_reflection.md` (both new), and `prompts/README.md`.

**Not touched**

- **`appsettings.json` and `appsettings.Development.json`**, in every environment.
- **`docs/CONFIGURATION.md`**: no new appsettings keys. It may note the two preview command-line options.
- `ComposeDigestActivity.cs` (`ComposeDigestOutput`) and `ComposeInsightJsonActivity.cs` (`ComposeInsightJsonOutput`).
- All orchestrators, including `InsightsReportOrchestrator` (Sec.4.14 is a finding only).
- `sql/29_freetier_digest_artifact.sql`.
- The weekly lane.
