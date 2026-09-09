namespace Insights.Domain;

/// <summary>
/// Design doc Sec.2.4's 30-day cooldown - at most one generation per (scope, report-type,
/// period). <see cref="NextAvailableUtc"/> is null exactly when <see cref="IsOpen"/> is true;
/// there is nothing to wait for once the window is open.
/// </summary>
public sealed record CooldownResult(bool IsOpen, DateTime? NextAvailableUtc);
