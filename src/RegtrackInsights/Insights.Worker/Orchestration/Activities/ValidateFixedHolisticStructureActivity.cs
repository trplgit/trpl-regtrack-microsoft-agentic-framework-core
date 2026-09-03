using DurableTask.Core;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ValidateFixedHolisticStructureInput(string Html);
public sealed record ValidateFixedHolisticStructureOutput(string Html);

/// <summary>
/// Deterministic structural gate on the rendered report (FixedHolisticStructureGate,
/// Insights.Presentation) - two structural invariants of the score-hero/fixed-tab template found
/// violated by a real render: exactly 7 score-component cards whenever a score hero exists, and a
/// "0" tab-nav badge on any pane marked data-blocked="true". CLAUDE.md Sec.11: a structural
/// invariant, not a data-sanity check - THROWS, never warns, same posture as NormalizeActivity.
///
/// [UPDATED 2026-09-01] Written when this was "a zero-risk no-op until the score-hero/fixed-tab
/// template is promoted out of the lab" - it has been: 05_report_html_fixed_holistic.md is now the
/// ONLY render prompt (PaidReportAgentsRegistration), the plain MVP template (05_report_html.md,
/// "compliance_health") was removed the same session. Both checks are LIVE on every render now,
/// not a no-op.
///
/// Runs AFTER Normalize/Sanitize (same ordering reasoning NormalizeActivity's own doc comment
/// gives for its second call) so it checks the actual HTML that will be persisted, not a
/// pre-sanitization draft DOMPurify might still alter.
/// </summary>
public sealed class ValidateFixedHolisticStructureActivity : AsyncTaskActivity<ValidateFixedHolisticStructureInput, ValidateFixedHolisticStructureOutput>
{
    protected override Task<ValidateFixedHolisticStructureOutput> ExecuteAsync(TaskContext context, ValidateFixedHolisticStructureInput input) => RunAsync(input);

    internal Task<ValidateFixedHolisticStructureOutput> RunAsync(ValidateFixedHolisticStructureInput input)
    {
        var result = FixedHolisticStructureGate.Evaluate(input.Html);
        if (!result.Approved)
            throw new OrchestrationRefusedException(
                "FIXED_HOLISTIC_STRUCTURE_INVALID",
                "We couldn't generate this report to our accuracy standard. Our team has been notified.",
                result.Violations);

        return Task.FromResult(new ValidateFixedHolisticStructureOutput(input.Html));
    }
}
