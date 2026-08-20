using Insights.Agents;
using Insights.Data;
using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// Pure logic, no database - PublishGate's claim-checker is a set-membership check with no I/O,
/// and the scope audit is exercised here through a hand-written fake rather than a real
/// SqlScopeRepository (that path is already covered by ScopeRepositoryTests against real UAT
/// data). What matters here is PublishGate's own behaviour: does it call the audit at all, and
/// does it turn ScopeAuditFailedException into a refusal rather than letting it propagate.
/// </summary>
public sealed class PublishGateTests
{
    private static readonly Assertion TenantAssertion = new(
        "A-TENANT", "overdue_pct", "tenant", 28.9m, null, null, null, null, null, null);

    private static readonly Assertion WorstAssertion = new(
        "A-WORST", "overdue_pct", "Branch-1012", 50.0m, 1, 13, 28.9m, 21.1m, AssertionDirection.Worse, null);

    private sealed class FakeScopeRepository(bool auditPasses) : IScopeRepository
    {
        public int AuditCallCount { get; private set; }

        public Task<IReadOnlyList<ScopePair>> GetScopePairsAsync(int userId, int customerId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("Not exercised by PublishGate.");

        public Task<ScopeClassification> ClassifyScopeAsync(int userId, int customerId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("Not exercised by PublishGate.");

        public Task<IReadOnlyList<ScopelessUser>> FindScopelessUsersAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("Not exercised by PublishGate.");

        public Task<ScopeAuditResult> AuditScopeAsync(int userId, int customerId, CancellationToken cancellationToken = default)
        {
            AuditCallCount++;
            return auditPasses
                ? Task.FromResult(new ScopeAuditResult(0, 0, 0))
                : throw new ScopeAuditFailedException(userId, customerId, new InvalidOperationException("out-of-scope rows detected"));
        }
    }

    [Fact]
    public async Task EvaluateAsync_Approves_WhenEveryCitedAssertionExistsAndScopeAuditPasses()
    {
        var scopeRepository = new FakeScopeRepository(auditPasses: true);
        var gate = new PublishGate(scopeRepository);
        var narrative = new NarrativeResult([
            new NarrativeBlockResult("location_table", "Branch-1012 runs at 50.0%...", ["A-WORST", "A-TENANT"]),
        ]);

        var result = await gate.EvaluateAsync(userId: 38, customerId: 29, narrative, [TenantAssertion, WorstAssertion]);

        Assert.True(result.Approved);
        Assert.Null(result.UserFacingRefusal);
        Assert.Empty(result.InternalDiagnostics);
        Assert.Equal(1, scopeRepository.AuditCallCount);
    }

    /// <summary>
    /// The whole point of the design: this is the D7 defence. A narrative citing an id nobody
    /// supplied is exactly a hallucinated-number scenario, and it must refuse before ever
    /// reaching the database - see the fail-fast comment in PublishGate itself.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_Refuses_WhenNarrativeCitesUnknownAssertionId()
    {
        var scopeRepository = new FakeScopeRepository(auditPasses: true);
        var gate = new PublishGate(scopeRepository);
        var narrative = new NarrativeResult([
            new NarrativeBlockResult("location_table", "Branch-1012 is somehow at 62%...", ["A-INVENTED"]),
        ]);

        var result = await gate.EvaluateAsync(userId: 38, customerId: 29, narrative, [TenantAssertion, WorstAssertion]);

        Assert.False(result.Approved);
        Assert.Equal(
            "We couldn't generate this report to our accuracy standard. Our team has been notified.",
            result.UserFacingRefusal);
        Assert.Contains(result.InternalDiagnostics, d => d.Contains("A-INVENTED", StringComparison.Ordinal));
        Assert.Equal(0, scopeRepository.AuditCallCount); // fail-fast: never reaches the DB call
    }

    [Fact]
    public async Task EvaluateAsync_Refuses_WhenScopeAuditFails()
    {
        var scopeRepository = new FakeScopeRepository(auditPasses: false);
        var gate = new PublishGate(scopeRepository);
        var narrative = new NarrativeResult([
            new NarrativeBlockResult("location_table", "Branch-1012 runs at 50.0%...", ["A-WORST", "A-TENANT"]),
        ]);

        var result = await gate.EvaluateAsync(userId: 38, customerId: 29, narrative, [TenantAssertion, WorstAssertion]);

        Assert.False(result.Approved);
        Assert.Equal(
            "We couldn't generate this report to our accuracy standard. Our team has been notified.",
            result.UserFacingRefusal);
        Assert.Contains(result.InternalDiagnostics, d => d.Contains("out-of-scope rows detected", StringComparison.Ordinal));
    }

    /// <summary>
    /// The user-facing message must NEVER carry the real reason - see PublishGateResult's own
    /// doc comment and design doc 11.3 ("never leak the reason"). This is the check that would
    /// catch someone accidentally wiring InternalDiagnostics into a response DTO later.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_UserFacingRefusal_NeverContainsInternalDetail()
    {
        var scopeRepository = new FakeScopeRepository(auditPasses: false);
        var gate = new PublishGate(scopeRepository);
        var narrative = new NarrativeResult([
            new NarrativeBlockResult("location_table", "prose", ["A-TENANT"]),
        ]);

        var result = await gate.EvaluateAsync(userId: 38, customerId: 29, narrative, [TenantAssertion]);

        Assert.DoesNotContain("out-of-scope", result.UserFacingRefusal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("29", result.UserFacingRefusal!);
    }
}
