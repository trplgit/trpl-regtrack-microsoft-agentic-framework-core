namespace Insights.Domain;

/// <summary>
/// Result of usp_Insights_AuditScope. Only ever constructed on the SUCCESS path -
/// the proc THROWs (51010) instead of returning a row when any violation is found,
/// so a caller holding one of these already knows every count is zero. See
/// <see cref="ScopeAuditFailedException"/> for the failure path.
/// </summary>
public sealed record ScopeAuditResult(int BranchViolations, int CategoryViolations, int PairViolations);
