using System.Text.Json.Serialization;

namespace Insights.Contracts;

/*  Response DTOs for the Insights endpoints (docs/API_CONTRACTS.md).

    Typed rather than anonymous objects for two reasons:
      - Swagger can only generate a schema from a declared type, so an anonymous object gives
        callers an endpoint with no documented response shape;
      - the contract stops being implicit. A rename here is a compile error, where a rename inside
        an anonymous object is a silently changed wire format.

    camelCase on the wire, matching the contract's own examples.                                  */

/// <summary>API_CONTRACTS.md Sec.1 - one entry in the tenant picker.</summary>
public sealed class EligibleTenantResponse
{
    [JsonPropertyName("tenantId")]
    public int TenantId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary><c>basic</c> or <c>pro</c>. Resolved paid-wins when a tenant is mid-transition.</summary>
    [JsonPropertyName("tier")]
    public string Tier { get; set; } = string.Empty;

    /// <summary><c>tenant_wide</c> | <c>functional</c> | <c>entity_scoped</c>.</summary>
    [JsonPropertyName("scopeClass")]
    public string ScopeClass { get; set; } = string.Empty;
}

/// <summary>
/// API_CONTRACTS.md Sec.1. An EMPTY list is a valid, successful answer - the secure-deny state
/// (spec Sec.5.6.3), not an error. Clients read the count: 0 deny, 1 skip the picker, more show it.
/// </summary>
public sealed class EligibleTenantsResponse
{
    [JsonPropertyName("tenants")]
    public IReadOnlyList<EligibleTenantResponse> Tenants { get; set; } = [];
}

/// <summary>API_CONTRACTS.md Sec.4 - one progress frame on the run stream.</summary>
public sealed class RunStatusResponse
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    /// <summary><c>queued</c> | <c>running</c> | <c>complete</c> | <c>failed</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>One of the seven contract stages, or null before the first checkpoint lands.</summary>
    [JsonPropertyName("stage")]
    public string? Stage { get; set; }

    [JsonPropertyName("stagesComplete")]
    public int StagesComplete { get; set; }

    [JsonPropertyName("stagesTotal")]
    public int StagesTotal { get; set; }

    /// <summary>
    /// USER-SAFE text only, and only on failure. Gate diagnostics ("reconciliation variance of 3
    /// on branch X") are what spec Sec.11.3 keeps off the wire - they go to logs and alerts.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
