# Phase 1c - Free Weekly Digest: build checklist

Status as of 19 Aug 2026. Phase 1c ships **first** by design (`CLAUDE.md` 9): it
exercises the dictionary, scope service and procs end to end with minimal LLM surface.

---

## Already built

- [x] Solution structure, central package management, CI skeleton, Dockerfile
- [x] `sql/01`-`06` + `99_rollback` written, installed on UAT, 20/20 objects live
- [x] `sql/06` gate + the ~15 aggregates, verified returning data
- [x] .NET wrappers for `sql/02` (golden), `sql/03` (scope), `sql/04` (entity + entitlement)
- [x] Domain models: ScopePair, ScopeClassification, EntityTree, EntityRollup,
      TenantShape, EntitlementGateResult, GoldenInvariantResult
- [x] Integration tests against real data for scope / entity / entitlement / golden
- [x] MCP Toolbox connected to production, read-only, verified db_datareader only

---

## Blocked - need an answer before the code can finish

- [x] ~~`templates/digest.html` and `templates/digest_fallback.txt`~~ - DONE. Both were
      already authored inline inside `free_digest_email.md`; extracted verbatim into
      real files on 19 Aug. Nothing to ask for.
- [ ] **Per-recipient opt-out store does not exist.** `sql/06` has a TODO where
      opt-outs should be subtracted. Spec 5.4: opt-out is durable and must SURVIVE
      tier changes, or an upgrade/downgrade cycle silently re-subscribes someone who
      asked to stop. Needs a new table - agree the shape first.
- [ ] **No tenant has product 18 mapped**, so the gate returns EXIT_ZERO_COST
      everywhere and the PROCEED path has never run. Testing it needs a ProductMapping
      row for a throwaway UAT tenant - the only write to an existing RegTrack table
      anywhere in this project. Get sign-off before doing it.

---

## Build order

### 1. Data layer
- [ ] `DigestGateResult` domain model (mirror `EntitlementGateResult`)
- [ ] `DigestAggregates` domain model - all ~15 numbers as one immutable record
- [ ] `IDigestRepository` + `SqlDigestRepository` wrapping
      `usp_Insights_FreeDigestGate` and `usp_Insights_FreeDigestAggregates`
- [ ] Integration tests against a real tenant (use 5 or 1363 on UAT)

### 2. Recipients and scope
- [ ] Resolve recipients from `UserCustomerMapping` where ProductID = 18
- [ ] Subtract opt-outs (needs the store above)
- [ ] Decide per-recipient vs tenant-wide aggregates - see nuance 6 below

### 3. Prompt + LLM
- [ ] Prompt loader reading `Agents:PromptDirectory` (`06_freetier_digest.md`)
- [ ] LLM client (Claude, first-party MAF provider)
- [ ] Enforce `Budget:FreeDigestTokenCap` (1500). Over budget = skip LLM, send template
- [ ] Idempotency key `(runId, nodeId)` so a replay does not double-bill

### 4. Validator - reject the LLM body and fall back if ANY fails
- [ ] A `%` adjacent to a completion/closure word (a ratio was invented)
- [ ] The word "overdue" anywhere (reserved for the paid tier)
- [ ] A location, user, department or Act name (it has no such data)
- [ ] **A number not present in the 15 aggregates** - numeric-token diff. Strongest check
- [ ] Over ~400 words

### 5. Render + send
- [ ] Token substitution for `digest.html`
- [ ] Fallback body renderer from `digest_fallback.txt`
- [ ] Email provider with SPF / DKIM / DMARC
- [ ] Bounce webhook - hard bounce = suppress recipient and flag ops
- [ ] Unsubscribe URL writing the durable per-recipient opt-out

### 6. Schedule + observability
- [ ] Weekly anchor `day_of_week = hash(tenantId) % 7`
- [ ] Lowest priority lane - must never delay a paying user
- [ ] Metrics: `insights.digest.sent_total` / `skipped_total {reason}`

---

## Nuances to keep in mind while building

**1. RiskType - do not trust UAT.** Dictionary says `3 = Critical`. Production
confirms it (98-99.8% of imprisonment items on RiskType 3 across all five validation
tenants; tenant 1490 reproduces the spec's 1,420/1,425 = 99.6%). **UAT's data is
different** - imprisonment sits on RiskType 0 there. So `CriticalDueNext7/30` will read
near-zero on UAT. That is UAT data, NOT a bug. Do not "fix" it. Validate any critical
figure against production.

**2. `ProductMapping.IsActive` is INVERTED** - 0 = ENABLED, 1 = disabled. Same for
`UserCustomerMapping.IsActive`. Never "correct" it.

**3. Entitlement is evaluated at JOB EXECUTION TIME**, never cached at schedule time.
That is what makes a Wednesday upgrade correctly skip Thursday's digest.

**4. Paid supersedes free.** Product 19 mapped = free digest self-skips. Also covers
the non-atomic window where both are briefly mapped.

**5. `UserCustomerMapping` is NOT a reliable user-to-tenant link** - many users
legitimately have zero rows. Use it ONLY for product/recipient mapping, never to infer
which tenant a user belongs to.

**6. `@UserID = NULL` means tenant-wide.** Only pass NULL when the recipient is verified
tenant-wide; otherwise pass their UserID so the 2-D scope constraint applies. A free
recipient must never receive numbers outside their authorised scope.

**7. Recency safety - non-negotiable.** Never compute a completion ratio over a recent
window (one tenant showed 272 of 322 not closed, ~84%, which is recency lag not
failure). Backward-looking content is ABSOLUTE COMPLETED COUNT ONLY. The word "overdue"
as a level is reserved for the paid tier.

**8. The email NEVER fails to go out.** Over budget, LLM error, or validator rejection
all fall back to the deterministic template and the send proceeds.

**9. Conversion boundary.** Show the WHAT, never the WHERE / WHO / WHY. The gap between
the number and its explanation is the sales pitch.

**10. UAT digest numbers look nearly empty** (tenant 5: DueNext7 = 8, DueNext30 = 26,
CompletedLast7 = 0) because UAT's schedules are stale. The spec assumes "nobody's weekly
email is empty" (66 due in 7 days on a small tenant). Check volumes against production
before concluding the windows are wrong.

**11. Two different tenant control totals exist.** `usp_Insights_EntityRollup`
reconciles tenant 5 against 9,742; the digest reports 9,607, because the digest joins
`Compliance ... IsDeleted = 0` and the rollup does not. ~135 instances point at
soft-deleted Compliance rows. Excluding them is right, but non-negotiable 3 says
per-dimension sums must tie to tenant control totals - resolve which is authoritative
before more dimensions land.

**12. A failed CREATE does not stop later batches.** Both `usp_Insights_StatusDataQuality`
and `usp_Insights_AuditScope` failed to create while the script still printed
"installed". After every SQL install, COUNT the objects - never trust the trailing PRINT.

---

## Still open outside Phase 1c

- [ ] `sql/05` Location dimension .NET wrapper (Phase 1b)
- [ ] CI fixture database, tenant 999001 (`docs/GOLDEN_FIXTURES.md`) - the only way to
      test the reconciliation THROWs and the on-time % = 50.0 assertion. Also what
      unblocks the disabled `golden` job in `.github/workflows/ci.yml`
- [ ] Phase 1a sign-off: 5th tenant on the golden suite, plus boundary tests
      (single-branch tenant, empty peer sample, empty scope THROW)
- [ ] `docs/RegTrack_Classification_Dictionary_v1.xlsx` missing from the repo

