using Microsoft.Playwright;

namespace Insights.Presentation;

public enum TileFindingSeverity { Cosmetic, Functional }

/// <summary>
/// [ADDED 2026-10-07] One real problem InteractiveTileChecker found while exercising a report's
/// interactive elements tile by tile. CardSelector/Interaction are stable enough to re-locate the
/// same element after a patch (see docs/superpowers/specs/2026-10-07-interactive-tile-qa-design.md
/// Section 1). Severity drives the orchestrator's patch-loop exhaustion behaviour (Section 4):
/// Functional means the control itself did not appear to do anything when triggered (own tile
/// showed no visible change); Cosmetic means triggering it visibly broke a DIFFERENT part of the
/// page (cross-tile bleed), confirmed by a real vision-model review of the before/after crop.
/// </summary>
public sealed record TileFinding(
    string CardSelector,
    string CardTitle,
    string Interaction,
    TileFindingSeverity Severity,
    string TechnicalDescription,
    string BeforeScreenshotPath,
    string AfterScreenshotPath);

public interface IInteractiveTileChecker
{
    Task<IReadOnlyList<TileFinding>> FindIssuesAsync(string html, CancellationToken cancellationToken = default);
}
