namespace Insights.Domain;

/// <summary>
/// One authorised (BranchID, CategoryId) tuple - the unit of authorisation.
/// Scope is two-dimensional; a branch alone is never enough (sql/03).
/// </summary>
/// <remarks>
/// BranchId/CategoryId are long: they come straight off EntitiesAssignment.BranchID /
/// ComplianceCatagoryID, both BIGINT - unlike the @UserID/@CustomerID parameters
/// that resolve them (declared INT on every proc in sql/03).
/// </remarks>
public sealed record ScopePair(long BranchId, long CategoryId);
