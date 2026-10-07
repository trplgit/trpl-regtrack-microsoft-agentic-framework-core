namespace Insights.Domain;

/// <summary>[ADDED 2026-10-07] ITileGlitchReviewAgent's output - judges one cropped before/after pair
/// InteractiveTileChecker's own cheap pixel-diff already flagged as changed. "Broken" means a reader
/// would see this as a defect (overlapping text, bleeding colour, something cut off or disappearing
/// that should not); "legitimate" means expected page content that happens to update on its own
/// (e.g. a live chart re-rendering with the same data, a focus ring appearing).</summary>
public sealed record TileGlitchReviewResult(bool IsBroken, string? Explanation);
