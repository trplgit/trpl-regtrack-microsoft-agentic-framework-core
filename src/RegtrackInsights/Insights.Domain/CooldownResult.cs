namespace Insights.Domain;

/// <summary>
/// [REDESIGNED 2026-09-25] Was "at most one generation per (scope, report-type, period)" - a real
/// caller could dodge the lock by simply sending a different Period string for the SAME dimension
/// (30 days vs 60 days vs a free-text value), since Period was part of the match. Real product
/// decision: the lock is per DIMENSION, full stop - once a dimension has a real completed report
/// within the cooldown window, it is locked for that scope regardless of what period text or
/// picker choice the next call uses. See <see cref="Insights.Data.ICooldownRepository"/>'s own doc
/// comment for the real key shape this now checks.
///
/// <see cref="NextAvailableUtc"/> and <see cref="DaysRemaining"/> are null exactly when
/// <see cref="IsOpen"/> is true; there is nothing to wait for once the window is open.
/// <see cref="DaysRemaining"/> is computed at check time (`Math.Ceiling` of the real remaining
/// span) - the real number the product wants to show verbatim: "there is a cooldown period and
/// this many days left."
/// </summary>
public sealed record CooldownResult(bool IsOpen, DateTime? NextAvailableUtc, int? DaysRemaining = null);
