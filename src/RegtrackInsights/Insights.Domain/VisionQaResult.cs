namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-09-14] The vision model's verdict on one or more real screenshots of a rendered
/// report. Deliberately NARROW scope, same "cosmetic only" spirit as ReportQaResult, but unlike
/// that one, THIS result gates the render-retry loop (InsightsReportOrchestrator) - a real
/// visual defect (overlap, content escaping its container, obviously broken layout) is treated
/// the same way a structure-gate violation already is: retry the whole render, not just log it.
///
/// <see cref="Issue"/> is what actually reaches the next render attempt (RenderHtmlInput.
/// PreviousVisualIssue) - concrete and specific enough that the render agent can fix the exact
/// problem, not a vague "something looked wrong".
/// </summary>
public sealed record VisionQaResult(bool HasVisualDefect, string? Issue);
