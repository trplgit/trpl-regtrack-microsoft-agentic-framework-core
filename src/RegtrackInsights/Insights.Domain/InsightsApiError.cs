namespace Insights.Domain;

/// <summary>
/// The five error codes API_CONTRACTS.md locks, and the HTTP status each maps to.
/// One place, so an endpoint cannot invent a sixth or map an existing one differently.
/// </summary>
public enum InsightsErrorCode
{
    /// <summary>403. Tenant is not in the caller's eligible set.</summary>
    TenantNotEligible,

    /// <summary>403. Entitled, but zero scope pairs - surface the §11.2 message, never an empty report.</summary>
    ScopeDenied,

    /// <summary>409. Includes nextAvailableUtc.</summary>
    CooldownActive,

    /// <summary>
    /// 404, deliberately NOT 403. report_scope is not a subset of viewer_scope.
    /// A 403 would confirm the report exists; a 404 reveals nothing.
    /// </summary>
    ReportNotVisible,
}

/// <summary>
/// The standard error envelope: <c>{ "error": { "code": "...", "message": "..." } }</c>.
/// </summary>
public sealed record InsightsApiError(string Code, string Message)
{
    public static InsightsApiError For(InsightsErrorCode code, string message) => new(ToWireName(code), message);

    public static string ToWireName(InsightsErrorCode code) => code switch
    {
        InsightsErrorCode.TenantNotEligible => "TENANT_NOT_ELIGIBLE",
        InsightsErrorCode.ScopeDenied => "SCOPE_DENIED",
        InsightsErrorCode.CooldownActive => "COOLDOWN_ACTIVE",
        InsightsErrorCode.ReportNotVisible => "REPORT_NOT_VISIBLE",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped error code."),
    };

    public static int ToStatusCode(InsightsErrorCode code) => code switch
    {
        InsightsErrorCode.TenantNotEligible => 403,
        InsightsErrorCode.ScopeDenied => 403,
        InsightsErrorCode.CooldownActive => 409,
        InsightsErrorCode.ReportNotVisible => 404,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped error code."),
    };
}

/// <summary>The wire shape. Wrapping exists so the JSON nests under "error" as the contract shows.</summary>
public sealed record InsightsApiErrorEnvelope(InsightsApiError Error);
