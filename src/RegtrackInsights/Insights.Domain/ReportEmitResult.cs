namespace Insights.Domain;

/// <summary>
/// Verdict of the deterministic Report Emit Normalizer (prompts/05_report_html_fixed_holistic.md "Output
/// constraints - every one is enforced downstream"). Any violation refuses the whole report -
/// there is no partial-render path here, unlike PublishGate's per-claim refusal; a document that
/// fails even one structural/security rule is not safe to hand to a sandboxed iframe at all.
/// </summary>
public sealed record ReportEmitResult(bool Approved, IReadOnlyList<string> Violations)
{
    public static readonly ReportEmitResult Approve = new(true, []);
    public static ReportEmitResult Refuse(IReadOnlyList<string> violations) => new(false, violations);
}
