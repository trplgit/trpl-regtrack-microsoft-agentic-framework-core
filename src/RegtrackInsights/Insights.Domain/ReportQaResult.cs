namespace Insights.Domain;

/// <summary>
/// Cosmetic render QA (Presentation:RunPlaywrightQa) - NEVER a security control. Security lives
/// entirely in ReportEmitNormalizer and DOMPurify, both of which already ran by the time this
/// executes. <see cref="HasIssues"/> is advisory: a real deployment logs/alerts on it, it does
/// not refuse the report the way a normalizer or gate violation does.
/// </summary>
public sealed record ReportQaResult(
    bool HasIssues,
    IReadOnlyList<string> ConsoleErrors,
    bool HasHorizontalOverflow,
    byte[] Screenshot);
