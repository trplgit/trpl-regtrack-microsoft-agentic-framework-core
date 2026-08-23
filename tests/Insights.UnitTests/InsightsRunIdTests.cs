using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// The run id does two load-bearing jobs: it IS the one-active-run-per-key lock (§4.5), and it
/// is what tells the progress endpoint which tenant to authorise against. Both fail silently if
/// it is wrong - a broken lock just double-spends tokens, and a mis-parsed tenant authorises
/// against the wrong customer while still returning a plausible 403 most of the time.
/// </summary>
public sealed class InsightsRunIdTests
{
    /// <summary>THE LOCK. Same key, same id - otherwise a double-click starts two runs.</summary>
    [Fact]
    public void For_IsDeterministicForTheSameKey()
    {
        var a = InsightsRunId.For(23, "tenant", "compliance_health", "FY2025-26");
        var b = InsightsRunId.For(23, "tenant", "compliance_health", "FY2025-26");

        Assert.Equal(a, b);
    }

    /// <summary>
    /// Casing and surrounding whitespace must not fork the bucket. "FY2025-26" arriving from one
    /// client and "fy2025-26 " from another are the same report, and treating them as two keys
    /// would let a caller bypass the 30-day cooldown just by changing case.
    /// </summary>
    [Fact]
    public void For_IsInsensitiveToCaseAndWhitespace()
    {
        var a = InsightsRunId.For(23, "tenant", "compliance_health", "FY2025-26");
        var b = InsightsRunId.For(23, " TENANT ", "Compliance_Health", " fy2025-26 ");

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(24, "tenant", "compliance_health", "FY2025-26")]      // different tenant
    [InlineData(23, "entity:92442", "compliance_health", "FY2025-26")] // different scope
    [InlineData(23, "tenant", "exposure", "FY2025-26")]                // different type
    [InlineData(23, "tenant", "compliance_health", "FY2024-25")]       // different period
    public void For_DiffersWhenAnyKeyComponentDiffers(int tenantId, string scope, string type, string period)
    {
        var baseline = InsightsRunId.For(23, "tenant", "compliance_health", "FY2025-26");

        Assert.NotEqual(baseline, InsightsRunId.For(tenantId, scope, type, period));
    }

    /// <summary>
    /// Separator discipline. Without a delimiter that cannot appear in a component, ("ab", "c")
    /// and ("a", "bc") hash identically - two unrelated reports sharing one 30-day cooldown.
    /// </summary>
    [Fact]
    public void For_DoesNotCollideAcrossComponentBoundaries()
    {
        var a = InsightsRunId.For(23, "ab", "c", "FY2025-26");
        var b = InsightsRunId.For(23, "a", "bc", "FY2025-26");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TryParse_RecoversTheTenantFromAGeneratedId()
    {
        var runId = InsightsRunId.For(1490, "tenant", "compliance_health", "FY2025-26");

        Assert.True(InsightsRunId.TryParse(runId, out var tenantId));
        Assert.Equal(1490, tenantId);
    }

    /// <summary>
    /// Everything malformed must be rejected, not coerced. "insights-23-" in particular: if the
    /// hash half were not validated it would parse as tenant 23, and every junk id in the world
    /// would land in one tenant's namespace.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("insights-23-")]                       // empty hash
    [InlineData("insights-23-nothex")]                 // wrong length and not hex
    [InlineData("insights-23-ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")] // right length, not hex
    [InlineData("insights-0-0000000000000000000000000000000000000000000000000000000000000000")]  // tenant 0
    [InlineData("insights--0000000000000000000000000000000000000000000000000000000000000000")]   // missing tenant
    [InlineData("freedigest-23-0000000000000000000000000000000000000000000000000000000000000000")] // another feature's id
    [InlineData("insights-23")]                        // truncated
    public void TryParse_RejectsMalformedIds(string? runId)
    {
        Assert.False(InsightsRunId.TryParse(runId, out var tenantId));
        Assert.Equal(0, tenantId);
    }

    /// <summary>
    /// A hyphen is the separator, so a negative tenant cannot round-trip: "insights--23-{hash}"
    /// splits into FOUR parts and is rejected on arity, never reaching the numeric guard. The
    /// test is named for what it actually proves - the earlier version claimed to exercise the
    /// non-positive branch and did not. That branch is reachable only via tenant 0, which the
    /// malformed-id theory above covers.
    /// </summary>
    [Fact]
    public void TryParse_RejectsAnIdWithAnExtraSeparator()
    {
        var hash = new string((char)0x61, 64);

        Assert.False(InsightsRunId.TryParse($"insights--23-{hash}", out var tenantId));
        Assert.Equal(0, tenantId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void For_RefusesAnInvalidTenant(int tenantId) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => InsightsRunId.For(tenantId, "tenant", "compliance_health", "FY2025-26"));

    [Theory]
    [InlineData("", "compliance_health", "FY2025-26")]
    [InlineData("tenant", "", "FY2025-26")]
    [InlineData("tenant", "compliance_health", "")]
    [InlineData("  ", "compliance_health", "FY2025-26")]
    public void For_RefusesAnEmptyKeyComponent(string scope, string type, string period) =>
        Assert.Throws<ArgumentException>(() => InsightsRunId.For(23, scope, type, period));
}
