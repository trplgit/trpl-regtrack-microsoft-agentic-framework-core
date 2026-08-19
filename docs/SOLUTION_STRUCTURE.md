# Solution Structure

Proposed layout for `trpl-regtrack-microsoft-agentic-framework-core`.

> **Adapt to match the existing RegTrack solution's conventions** — namespace
> style, folder layout, `Directory.Build.props`, analyzer settings. Inspect the
> API repo before finalising. Do not introduce a parallel style (`CLAUDE.md` §8).

```
trpl-regtrack-microsoft-agentic-framework-core/
├── CLAUDE.md
├── README.md
├── RegtrackInsights.sln
├── Directory.Build.props / Directory.Packages.props / global.json
├── docs/            (spec, dimensions, API, fixtures, config, this file)
├── sql/             (01—06, 99_rollback)
├── src/
│   └── RegtrackInsights/          ← the single project; the .csproj lives HERE
│       ├── Program.cs
│       ├── appsettings*.json      (local-only, gitignored)
│       ├── Properties/            (launchSettings)
│       ├── prompts/               (agent prompts — content, not embedded strings)
│       ├── templates/             (email templates)
│       ├── Insights.Contracts/    ← published; the API references THIS
│       ├── Insights.Domain/       ← dictionary, scope model, dimension contracts
│       ├── Insights.Data/         ← Dapper, stored-proc wrappers
│       ├── Insights.Agents/       ← MAF workflow, agent nodes, prompt loading
│       ├── Insights.Presentation/ ← normalizer, sanitiser, Playwright QA
│       └── Insights.Worker/       ← host: BackgroundService + Durable Task
└── tests/
    ├── Insights.UnitTests/
    ├── Insights.IntegrationTests/   ← runs against the fixture database
    └── Insights.GoldenTests/        ← wraps usp_Insights_GoldenInvariants
```

**The Insights.* entries under `src/RegtrackInsights/` are FOLDERS today, not projects.**
Phase 1a ships as a single project; they are split into real projects once they have
content and the dependency graph below is settled.

**Layout rule — this is load-bearing, not cosmetic.** The SDK's default globs are rooted
at the *project* directory and exclude only that project's own `bin\`/`obj\`. A project
at the repo root therefore globs `tests\**\bin`, copies it into its own output, then
globs that copy next build — nesting until paths exceed MAX_PATH and the build dies in
MSB3030 before the compiler runs. Keep every project a sibling under `src/` or `tests/`.
**Never place a project directory above another project directory** — when the Insights.*
folders become projects they move OUT to `src/Insights.X/`, as siblings of
`src/RegtrackInsights/`, never as children of it.

## Project responsibilities

| Project | Contains | Depends on |
|---|---|---|
| **Contracts** | DTOs the API needs: `EligibleTenant`, `ReportSummary`, `GenerateRequest`, `RunStatus`. **No logic.** | — |
| **Domain** | `ScopeResult`, `ScopePair`, `DimensionResult`, `Assertion`, `Finding`, `StatusClassification`, detector-policy logic | Contracts |
| **Data** | Dapper repositories, `QueryMultiple` mapping of the 6 result sets, `IScopeRepository`, `IDimensionRepository` | Domain |
| **Agents** | MAF `WorkflowBuilder` graph, composition/narrative/reflection nodes, prompt loader, claim-checker | Domain, Data |
| **Presentation** | Report Emit Normalizer, DOMPurify wrapper, Playwright runner, blob writer + envelope encryption | Domain |
| **Worker** | Host, Durable Task registration, orchestration + activities, scheduler, DI composition root | all |

## What the API consumes

The RegTrack API references **`Insights.Contracts` and `Insights.Data` only** —
enough to validate eligibility, read history, enqueue a job, and fetch a finished
artifact. It must **not** reference `Agents` or `Worker`.

Publish as a NuGet package from this repo (preferred — Insights owns its logic), or
as a project reference if the repos build together. Decide by inspecting how
packages are published today.

## Durable Task shape  (§3.7)

> **[TRAP] The orchestrator body must be deterministic.** No LLM calls, no
> `GETDATE()`, no DB access, no randomness in the orchestrator itself — all of that
> belongs in **activities**. This is not a constraint the design fights; it is
> exactly the architecture in §3.1.

```csharp
// Orchestrator — control flow ONLY
var scope   = await ctx.CallActivityAsync<ScopeResult>("ResolveScope", input);
var dims    = await ctx.CallActivityAsync<DimensionSet>("FetchDimensions", scope);
var asserts = await ctx.CallActivityAsync<AssertionSet>("BuildAssertions", dims);
var plan    = await ctx.CallActivityAsync<CompositionPlan>("Compose", asserts);   // LLM
var prose   = await ctx.CallActivityAsync<Narrative>("Narrate", (plan, asserts)); // LLM
await ctx.CallActivityAsync("PublishGate", (prose, asserts));   // THROWs to refuse
await ctx.CallActivityAsync("RenderAndStore", …);
```

**Enable orchestration versioning from day one.** Replay against changed workflow
code throws non-determinism errors otherwise.

**LLM activities must be idempotent** — key results by `(runId, nodeId)` so a
replay does not double-bill tokens.

## Local development

```
Required: .NET 8 SDK, SQL Server (or Azure SQL), Node (for Playwright)
Optional: Azurite (blob), Key Vault emulator or dev secrets

1. Restore a non-production vitComplianceSystem copy
2. Run sql/01–06 against it
3. dotnet user-secrets set "Llm:ApiKey" …
4. dotnet run --project src/RegtrackInsights
5. Trigger a run via the test harness in tests/Insights.IntegrationTests
```

Do not point local development at production. The worker writes blobs and index
rows; a misconfigured local run against prod would create real artifacts.
