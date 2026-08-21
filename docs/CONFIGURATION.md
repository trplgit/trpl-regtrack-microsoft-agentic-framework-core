# Configuration Reference

Every tunable in the system, with the value the design assumes and the section
that justifies it. **Nothing here should be hard-coded in application code.**

Values marked ⚠ change behaviour that was deliberately reasoned about — read the
spec section before altering them.

---

## Connections

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:RegTrack` | — | `vitComplianceSystem`. Read-only-ish: Insights writes only to `Insights*` tables |
| `ConnectionStrings:DurableTaskHub` | — | May be the same server, **separate database recommended** (§3.2) |
| `Azure:KeyVaultUri` | — | Reuses the DocAI envelope-encryption pattern (§9.2) |
| `Azure:BlobContainer` | `insights-reports` | Tenant-isolated by path prefix |

## LLM

| Key | Default | Notes |
|---|---|---|
| `Llm:Provider` | `anthropic` | First-party MAF provider (§3.2) |
| `Llm:Model` | — | Set per environment |
| `Llm:ApiKeySecretName` | — | Key Vault reference — **never in appsettings** |
| `Llm:RequestsPerMinute` | — | ⚠ Sizes the concurrency governor (§4.4) |
| `Llm:TokensPerMinute` | — | ⚠ The **binding constraint** on throughput, not worker count |

## Budgets and circuit breakers  (§12.3 — launch requirement)

| Key | Default | Notes |
|---|---|---|
| `Budget:PerRunTokenCeiling` | `250000` | ⚠ A run exceeding this aborts to the **refusal path**, not to a truncated report |
| `Budget:PerTenantMonthlyTokenCeiling` | `5000000` | ⚠ Ops safety valve. Should never trigger normally |
| `Budget:FreeDigestTokenCap` | `1500` | ⚠ Over budget ⇒ **skip the LLM, send the template**. The email never fails to go out (§10.5) |
| `Budget:AlertAtPercentOfCeiling` | `80` | Warn before the breaker trips |

## Report lifecycle

| Key | Default | Notes |
|---|---|---|
| `Reports:CooldownDays` | `30` | ⚠ Per `(scope, reportType, period)`. Consumed **only on success** (§2.4, §11.5) |
| `Reports:RetentionMonths` | `24` | ⚠ Auto-purge from generation date; DPDP-relevant (§9.4) |
| `Reports:KeepWarmWindowDays` | `90` | Only re-run combinations viewed within this window (§4.3) |
| `Reports:SasLifetimeMinutes` | `10` | Short-lived, single-use (§9.3) |
| `Reports:AllowInternalAdminOverride` | `true` | ⚠ Regtrack staff only — **never tenant users** (§2.4) |

## Scheduling  (§4.2)

| Key | Default | Notes |
|---|---|---|
| `Schedule:PaidAnchorModulo` | `28` | `day_of_month = hash(tenantId) % 28` — spreads ~600 tenants |
| `Schedule:FreeAnchorModulo` | `7` | `day_of_week = hash(tenantId) % 7` |
| `Schedule:LanePriorities` | `paid_interactive,paid_batch,free_weekly` | ⚠ Free must never delay a paying user |

## Agents  (§3.5)

| Key | Default | Notes |
|---|---|---|
| `Agents:MaxReflectionIterations` | `2` | ⚠ Bounded — reflection consumes tokens |
| `Agents:PromptDirectory` | `./prompts` | Prompts are files, not string literals |
| `Agents:IdempotencyCacheTtlHours` | `24` | ⚠ LLM activities keyed by `(runId, nodeId)` so a Durable Task **replay does not double-bill** (§3.7) |

## Detector policy  ⚠ (`CLAUDE.md` §4 — read before changing)

| Key | Default | Notes |
|---|---|---|
| `Detectors:AggregateThresholdPct` | `20.0` | Above this share ⇒ one aggregate finding, individuals suppressed |
| `Detectors:MaxIndividualFindings` | `5` | Cap per detector, ranked by materiality |
| `Detectors:MaterialityFloorInstances` | `50` | Minimum size to be assessed; drives the peer sample |
| `Detectors:PeerRelativeThresholdPct` | `20.0` | Flag below this share of the tenant median |
| `Detectors:HighOwnerlessPct` | `10.0` | Ownerless share that flags a member |

> These defaults were derived by measuring flag rates across 8 production tenants
> (they ranged 1.3%–84% under absolute thresholds). Changing them without
> re-measuring will reintroduce finding explosions.

## Presentation  (§8)

| Key | Default | Notes |
|---|---|---|
| `Presentation:Mode` | `way1` | ⚠ `way1` (LLM HTML) or `way2` (trusted renderer, Phase 2) |
| `Presentation:PerTenantKillSwitch` | — | Tenant ids with rendering disabled. **Must work without redeploy** |
| `Presentation:RunPlaywrightQa` | `true` | Cosmetic QA only — **never a security control** |
| `Presentation:CspPolicy` | see §8.2 | `connect-src 'none'`, `form-action 'none'`, self-origin only |

## Email  (§10.8)

| Key | Default | Notes |
|---|---|---|
| `Email:Provider` | — | Must support SPF/DKIM/DMARC |
| `Email:FromAddress` / `FromName` | — | |
| `Email:UnsubscribeBaseUrl` | — | Writes the **durable per-recipient opt-out** (§5.4) |
| `Email:BounceWebhookPath` | — | |
| `Email:TemplatePath` | `./templates` | |

## Feature flags

| Key | Default | Notes |
|---|---|---|
| `Features:CustomDimensions` | `false` | ⚠ Phase 2 — architecturally present, **off** |
| `Features:Snapshots` | `false` | ⚠ Phase 2 — gates delta and Q&A |
| `Features:CrossTenantRollup` | `false` | ⚠ Phase 2 (§5.6.5) |

## Observability  (§13)

| Key | Default | Notes |
|---|---|---|
| `Otel:LangfuseEndpoint` | — | OTLP |
| `Otel:EnableSensitiveData` | `false` | ⚠ `true` = internal full-trace projection; `false` = PII-scrubbed customer projection |
| `Otel:FilterInstrumentationScopes` | HTTP, EF | Or LangFuse fills with framework noise |

### Metrics to emit (names are a suggested convention)

```
insights.run.duration_seconds          {tenant, report_type, outcome}
insights.run.tokens_total              {tenant, report_type, node}
insights.run.cost_usd                  {tenant, report_type}
insights.gate.refusals_total           {reason}        ← ALERT on any
insights.scope.denials_total           {reason}        ← provisioning signal
insights.queue.depth                   {lane}
insights.governor.saturation_pct
insights.dimension.block_failures_total {dimension}
insights.digest.sent_total / skipped_total {reason}
```

**Alert on:** any publish-gate refusal (always — it means a data or dictionary
problem); sustained transient failures; per-tenant budget above
`AlertAtPercentOfCeiling`; keep-warm batch not draining in its window; repeated
failures of the same dimension block.
