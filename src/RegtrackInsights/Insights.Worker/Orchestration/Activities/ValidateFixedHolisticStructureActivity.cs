using DurableTask.Core;
using Insights.Domain;
using Insights.Presentation;

namespace Insights.Worker.Orchestration.Activities;

public sealed record ValidateFixedHolisticStructureInput(string Html, string ReportType);
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
/// [BUG FOUND LIVE, 2026-09-09] The class doc comment on FixedHolisticStructureGate used to claim
/// running this unconditionally on every report was safe because a non-fixed-holistic document
/// "has neither a score hero nor any data-blocked pane" - false in practice. A real
/// "dimension_selection:Departments" render legitimately reused the real Angular product's own
/// ".di-components" class for a non-score strip (the real detailed-insights.component.html reuses
/// that exact class for the Users role strip AND the Departments occurrence-status strip, not just
/// the composite-score row) and was refused 5/5 times: "score hero present but found 0
/// .di-component card(s), expected exactly 7". These checks are fixed_holistic-template-specific
/// invariants by construction - DimensionSelectionComposition never computes a composite score at
/// all (see its own doc comment) - so this activity now takes ReportType and skips evaluation
/// entirely for every ReportType except FixedHolisticComposition.ReportType, rather than relying on
/// a class-name coincidence that turned out not to hold.
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
        if (input.ReportType != FixedHolisticComposition.ReportType)
            return Task.FromResult(new ValidateFixedHolisticStructureOutput(input.Html));

        var result = FixedHolisticStructureGate.Evaluate(input.Html);
        if (!result.Approved)
            throw new OrchestrationRefusedException(
                "FIXED_HOLISTIC_STRUCTURE_INVALID",
                "We couldn't generate this report to our accuracy standard. Our team has been notified.",
                result.Violations);

        return Task.FromResult(new ValidateFixedHolisticStructureOutput(input.Html));
    }
}
