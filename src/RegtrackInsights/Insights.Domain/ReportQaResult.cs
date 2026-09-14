namespace Insights.Domain;

/// <summary>
/// Cosmetic render QA (Presentation:RunPlaywrightQa) - NEVER a security control. Security lives
/// entirely in ReportEmitNormalizer and DOMPurify, both of which already ran by the time this
/// executes. <see cref="HasIssues"/> is advisory: a real deployment logs/alerts on it, it does
/// not refuse the report the way a normalizer or gate violation does.
/// </summary>
/// <param name="Screenshots">
/// [CHANGED 2026-09-14] Was a single Screenshot - now one real full-page PNG per tab/state the
/// document exposes (the default view, plus one more per real `.di-tab` element clicked in turn -
/// see PlaywrightReportQa's own doc comment). Always at least one entry. A document with no tabs
/// (a single long scroll - explicitly allowed by every freehand render prompt) has exactly one.
/// </param>
public sealed record ReportQaResult(
    bool HasIssues,
    IReadOnlyList<string> ConsoleErrors,
    bool HasHorizontalOverflow,
    IReadOnlyList<byte[]> Screenshots);
