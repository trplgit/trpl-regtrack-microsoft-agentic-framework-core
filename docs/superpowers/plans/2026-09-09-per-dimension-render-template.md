# Per-Dimension Render Template Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let `RenderHtmlActivity` pick a dimension-specific render template for a single-dimension `dimension_selection` request, falling back to today's generic template when none is registered for that dimension yet.

**Architecture:** One additive change to `RenderHtmlActivity`'s existing agent-lookup logic — try a `"{ReportType}:{DimensionName}"` key first (only when the composition plan has exactly one block), fall back to the existing plain `ReportType` key. No new types, no new files, no change to `RenderHtmlInput`'s shape (the dimension name comes from `input.Plan.Blocks[0].Block`, already present).

**Tech Stack:** .NET 8, xUnit + Moq (matches the file's existing test style exactly).

**Spec:** `docs/superpowers/specs/2026-09-09-per-dimension-render-template-design.md`

## Global Constraints

- Byte-identical behavior when zero dimension-specific keys are registered (today's actual state) - every existing caller/test must keep passing unchanged.
- No new field on `RenderHtmlInput` - the dimension name is derived from `input.Plan.Blocks[0].Block`, never passed separately (spec Sec.4.1 - avoids a value that could drift from the plan's own content).
- Multi-dimension requests (`Plan.Blocks.Count > 1`) never consider a dimension-specific key - always the plain `ReportType` key (spec Sec.4.2).
- Unrecognized `ReportType` (neither key matches) still throws `InvalidOperationException`, fail-closed, unchanged message shape.

---

## Task 1: Dimension-specific render-agent lookup

**Files:**
- Modify: `src/RegtrackInsights/Insights.Worker/Orchestration/Activities/RenderHtmlActivity.cs`
- Modify: `tests/Insights.UnitTests/RenderHtmlActivityTests.cs`

**Interfaces:**
- Consumes: `input.Plan.Blocks` (existing `CompositionPlan.Blocks`, `IReadOnlyList<CompositionBlockPlan>`, each with a `.Block` string field) - already present on every `RenderHtmlInput`, no change needed to produce it.
- Produces: no new public interface - this is a behavior change inside `RenderHtmlActivity.RunAsync`, consumed the same way by every existing caller (`InsightsReportOrchestrator`, the lab tests) with zero signature change.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Insights.UnitTests/RenderHtmlActivityTests.cs` (after the existing two tests, before the closing `}`):

```csharp
    /// <summary>
    /// Design spec Sec.4.1 - a dimension-specific key ("{ReportType}:{DimensionName}") is tried
    /// first when the plan has exactly one block, ahead of the plain ReportType key. Uses
    /// DimensionSelectionComposition.Build's real block-naming (the block's Block field IS the
    /// dimension name, e.g. "Location" - not a synthetic string invented for this test).
    /// </summary>
    [Fact]
    public async Task RunAsync_SingleDimensionRequest_PrefersTheDimensionSpecificAgentWhenRegistered()
    {
        var genericAgent = new Mock<IReportHtmlAgent>();
        var locationAgent = new Mock<IReportHtmlAgent>();
        var plan = DimensionSelectionComposition.Build(["Location"]);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<Assertion> assertions = [];

        locationAgent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html>location, finalized</html>", 2000));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent>
        {
            [DimensionSelectionComposition.ReportType] = genericAgent.Object,
            [$"{DimensionSelectionComposition.ReportType}:Location"] = locationAgent.Object,
        });

        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt));

        Assert.Equal("<!DOCTYPE html><html>location, finalized</html>", result.Html);
        genericAgent.Verify(a => a.RenderAsync(It.IsAny<CompositionPlan>(), It.IsAny<NarrativeResult>(), It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<LocationRow>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Regression guard - today's exact behavior when no dimension-specific agent is registered yet (the real current state for every dimension).</summary>
    [Fact]
    public async Task RunAsync_SingleDimensionRequest_FallsBackToGenericAgentWhenNoDimensionSpecificOneRegistered()
    {
        var genericAgent = new Mock<IReportHtmlAgent>();
        var plan = DimensionSelectionComposition.Build(["Nature"]);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<Assertion> assertions = [];

        genericAgent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html>nature, generic</html>", 1800));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent>
        {
            [DimensionSelectionComposition.ReportType] = genericAgent.Object,
        });

        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt));

        Assert.Equal("<!DOCTYPE html><html>nature, generic</html>", result.Html);
    }

    /// <summary>Design spec Sec.4.2 - a multi-dimension request never considers a dimension-specific key, even if one happens to be registered for one of the requested dimensions.</summary>
    [Fact]
    public async Task RunAsync_MultiDimensionRequest_AlwaysUsesTheGenericAgent_NeverADimensionSpecificOne()
    {
        var genericAgent = new Mock<IReportHtmlAgent>();
        var locationAgent = new Mock<IReportHtmlAgent>();
        var plan = DimensionSelectionComposition.Build(["Location", "Users"]);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<Assertion> assertions = [];

        genericAgent.Setup(a => a.RenderAsync(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentCallResult<string>("<!DOCTYPE html><html>location+users, generic</html>", 3200));

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent>
        {
            [DimensionSelectionComposition.ReportType] = genericAgent.Object,
            [$"{DimensionSelectionComposition.ReportType}:Location"] = locationAgent.Object,
        });

        var result = await activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", DimensionSelectionComposition.ReportType, generatedAt));

        Assert.Equal("<!DOCTYPE html><html>location+users, generic</html>", result.Html);
        locationAgent.Verify(a => a.RenderAsync(It.IsAny<CompositionPlan>(), It.IsAny<NarrativeResult>(), It.IsAny<IReadOnlyList<Assertion>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<LocationRow>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Unchanged - an unrecognized ReportType still throws, fail-closed, regardless of this change.</summary>
    [Fact]
    public async Task RunAsync_UnrecognisedReportType_StillThrows()
    {
        var plan = new CompositionPlan(new CompositionHero("snapshot", "why"), [], [], []);
        var narrative = new NarrativeResult([]);
        var generatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<Assertion> assertions = [];

        var activity = new RenderHtmlActivity(new Dictionary<string, IReportHtmlAgent>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            activity.RunAsync(new RenderHtmlInput(plan, narrative, assertions, "Tenant 29 (UAT)", "no_such_report_type", generatedAt)));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Insights.UnitTests --filter "FullyQualifiedName~RenderHtmlActivityTests"`
Expected: the 4 new tests FAIL - `RunAsync_SingleDimensionRequest_PrefersTheDimensionSpecificAgentWhenRegistered` and `RunAsync_MultiDimensionRequest_...` fail because the dimension-specific key is never tried yet (both agents' setups exist, but the wrong one - or the only registered generic one when a specific key doesn't exist - gets called, or `TryGetValue` on a key that was never looked up leaves the mock unverified in a way that fails `Times.Never`... concretely: today's code only ever looks up `input.ReportType` directly, so the `"dimension_selection:Location"` entry is simply never read, and `RunAsync_SingleDimensionRequest_PrefersTheDimensionSpecificAgentWhenRegistered` fails because `genericAgent` (not `locationAgent`) is the one actually invoked, products the wrong HTML string ("generic" text instead of "finalized" text) - the assertion on `result.Html` fails. The other two new tests (fallback, multi-dimension) pass immediately since they already match today's only lookup path - that's fine, they exist as regression guards for Step 4, not as new-behavior proof.

- [ ] **Step 3: Implement the two-step lookup**

In `src/RegtrackInsights/Insights.Worker/Orchestration/Activities/RenderHtmlActivity.cs`, replace the current lookup:

```csharp
        if (!htmlAgentsByReportType.TryGetValue(input.ReportType, out var htmlAgent))
            throw new InvalidOperationException(
                $"No render agent registered for ReportType '{input.ReportType}'. Registered: {string.Join(", ", htmlAgentsByReportType.Keys)}.");
```

with:

```csharp
        // Design spec (docs/superpowers/specs/2026-09-09-per-dimension-render-template-design.md
        // Sec.4.1) - a single-dimension request tries a dimension-specific key first
        // ("{ReportType}:{DimensionName}"), falling back to the plain ReportType key when no
        // dimension-specific template is registered yet (today's state for every dimension).
        // The dimension name comes from the plan itself (Plan.Blocks[0].Block) - never a new
        // field that could drift from what the plan actually says.
        var specificKey = input.Plan.Blocks.Count == 1 ? $"{input.ReportType}:{input.Plan.Blocks[0].Block}" : null;

        if ((specificKey is null || !htmlAgentsByReportType.TryGetValue(specificKey, out var htmlAgent))
            && !htmlAgentsByReportType.TryGetValue(input.ReportType, out htmlAgent))
        {
            throw new InvalidOperationException(
                $"No render agent registered for ReportType '{input.ReportType}'. Registered: {string.Join(", ", htmlAgentsByReportType.Keys)}.");
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Insights.UnitTests --filter "FullyQualifiedName~RenderHtmlActivityTests"`
Expected: PASS, all 6 (2 pre-existing + 4 new).

- [ ] **Step 5: Run the full unit suite**

Run: `dotnet test tests/Insights.UnitTests`
Expected: 100% pass, zero regressions - this change is purely additive to one method's lookup logic, every other caller of `RenderHtmlActivity` passes a plan whose `Blocks.Count` is either 0 (never matches, falls through immediately) or already relies on the plain `ReportType` key today, which stays the final fallback.

- [ ] **Step 6: Commit**

```bash
git add src/RegtrackInsights/Insights.Worker/Orchestration/Activities/RenderHtmlActivity.cs tests/Insights.UnitTests/RenderHtmlActivityTests.cs
git commit -m "feat: RenderHtmlActivity prefers a dimension-specific render template when registered

Additive - a single-dimension dimension_selection request now tries a
\"{ReportType}:{DimensionName}\" key first, falling back to the existing
plain ReportType key when nothing dimension-specific is registered yet
(today's state for all 9 dimensions). No new field on RenderHtmlInput -
the dimension name comes from Plan.Blocks[0].Block, already present.
Multi-dimension requests are unaffected - always the plain key.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01WJG2rY2CxtUy1UrY7kSsio"
```

---

## Self-Review Notes

- **Spec coverage:** Sec.4.1 (two-step lookup) - Task 1 Step 3. Sec.4.2 (multi-dimension unaffected) - Task 1's third test. Sec.5 (error handling unchanged) - Task 1's fourth test. Sec.6 (all 4 listed test cases) - all present, one test each. Sec.7 (out of scope: frontend, real per-dimension prompts) - correctly not attempted by this plan.
- **Placeholder scan:** none - every step has real, complete code.
- **Type consistency:** `DimensionSelectionComposition.Build(IReadOnlyList<string>)`, `.ReportType` (const string), `CompositionPlan.Blocks[i].Block` (string) all match their real definitions already in the codebase (verified against the actual files before writing this plan, not assumed).
