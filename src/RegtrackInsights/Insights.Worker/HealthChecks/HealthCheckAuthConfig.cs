namespace Insights.Worker.HealthChecks;

/// <summary>
/// Binds the "HealthCheckAuth" section of appsettings.json. The token gates /health,
/// /health/ready and /health/live - same convention as the sibling RegTrack API
/// (trpl-regtrack-dot-net-core-api), which faces the same shape of problem: once this host sits
/// behind an ingress, /health/ready would otherwise publish this pod's internal readiness state
/// to anyone who can reach it.
/// </summary>
public sealed class HealthCheckAuthConfig
{
    /// <summary>appsettings.json / environment key this binds to.</summary>
    public const string SectionName = "HealthCheckAuth";

    /// <summary>
    /// Shared secret callers must send in the X-Health-Token request header. In Kubernetes this
    /// should come from a Secret as the HealthCheckAuth__Token environment variable, never from
    /// appsettings.json in a real environment - see this repo's existing Key Vault convention
    /// (CONFIGURATION.md, Llm:ApiKeySecretName). Leave blank to disable the gate (the endpoints
    /// then stay anonymous, and startup logs an error) - see HealthCheckTokenMiddleware for why
    /// this fails OPEN rather than closed.
    /// </summary>
    public string? Token { get; set; }
}
