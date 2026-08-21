namespace Insights.Domain;

/// <summary>
/// A user mapped to RegInsights (product 18 or 19) with zero EntitiesAssignment
/// scope rows - "entitled but scopeless". Fail-closed correctly returns an empty
/// report for them, which is safe but looks broken, and is how security boundaries
/// erode socially under support pressure (pre-mortem D5). Provisioning-time signal,
/// not a runtime path.
/// </summary>
public sealed record ScopelessUser(int CustomerId, int UserId, int ProductId, string ProductName, bool UserIsActive, string Issue);
