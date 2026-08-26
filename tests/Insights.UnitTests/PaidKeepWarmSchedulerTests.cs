using Insights.Data;
using Insights.Domain;
using Insights.Worker;
using Microsoft.EntityFrameworkCore;

namespace Insights.UnitTests;

/// <summary>
/// The paid_batch keep-warm lane (design doc Sec.4.2-4.5). Two things worth pinning hard:
/// the anchor-day math never randomises across process restarts, and the candidate query only
/// ever picks the LATEST row per (scope, reportType, period) key - an older row satisfying the
/// filters must never leak a stale candidate back in once a newer generation exists.
/// </summary>
public sealed class PaidKeepWarmSchedulerTests
{
    private const int TenantId = 29;
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ViewedSinceUtc = Now.AddDays(-90);
    private static readonly DateTime CooldownBeforeUtc = Now.AddDays(-30);

    private static InsightsReportsDbContext NewInMemoryDb() =>
        new(new DbContextOptionsBuilder<InsightsReportsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static GeneratedReport Report(
        int customerId, string scopeDescriptor, string reportType, string period,
        DateTime generatedAtUtc, DateTime? lastViewedUtc) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customerId,
        ScopeDescriptor = scopeDescriptor,
        ReportType = reportType,
        Period = period,
        GeneratedAtUtc = generatedAtUtc,
        GeneratedByUserId = 38,
        BlobContainer = "insights-reports-temp",
        BlobPath = "abc123.dat",
        Status = "complete",
        EncryptedAesKey = [1, 2, 3],
        KeyVaultObjectName = "TRPLCryptoKey-001",
        KeyVaultObjectVersion = "https://vault.azure.net/keys/TRPLCryptoKey-001/abc123",
        LastViewedUtc = lastViewedUtc,
    };

    [Fact]
    public async Task Candidates_IncludesAKey_ViewedRecentlyAndPastCooldown()
    {
        using var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report(TenantId, "tenant", "compliance_health", "FY2025-26",
            generatedAtUtc: Now.AddDays(-40), lastViewedUtc: Now.AddDays(-10)));
        await db.SaveChangesAsync();

        var candidates = await PaidKeepWarmScheduler.GetKeepWarmCandidatesAsync(db, TenantId, ViewedSinceUtc, CooldownBeforeUtc, CancellationToken.None);

        Assert.Single(candidates);
    }

    [Fact]
    public async Task Candidates_ExcludesAKey_NeverViewed()
    {
        using var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report(TenantId, "tenant", "compliance_health", "FY2025-26",
            generatedAtUtc: Now.AddDays(-40), lastViewedUtc: null));
        await db.SaveChangesAsync();

        var candidates = await PaidKeepWarmScheduler.GetKeepWarmCandidatesAsync(db, TenantId, ViewedSinceUtc, CooldownBeforeUtc, CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Candidates_ExcludesAKey_ViewedOutsideTheKeepWarmWindow()
    {
        using var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report(TenantId, "tenant", "compliance_health", "FY2025-26",
            generatedAtUtc: Now.AddDays(-120), lastViewedUtc: Now.AddDays(-100)));
        await db.SaveChangesAsync();

        var candidates = await PaidKeepWarmScheduler.GetKeepWarmCandidatesAsync(db, TenantId, ViewedSinceUtc, CooldownBeforeUtc, CancellationToken.None);

        Assert.Empty(candidates);
    }

    /// <summary>[TRAP this guards] Regenerated 5 days ago - re-running again now would blow past the 30-day cooldown (Sec.4.5).</summary>
    [Fact]
    public async Task Candidates_ExcludesAKey_StillWithinCooldown()
    {
        using var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report(TenantId, "tenant", "compliance_health", "FY2025-26",
            generatedAtUtc: Now.AddDays(-5), lastViewedUtc: Now.AddDays(-1)));
        await db.SaveChangesAsync();

        var candidates = await PaidKeepWarmScheduler.GetKeepWarmCandidatesAsync(db, TenantId, ViewedSinceUtc, CooldownBeforeUtc, CancellationToken.None);

        Assert.Empty(candidates);
    }

    /// <summary>THE ONE THAT MATTERS. Two rows, same key - only the newer generation's own view/cooldown state may decide the outcome.</summary>
    [Fact]
    public async Task Candidates_ForARepeatedKey_JudgesOnlyTheLatestRow_NotAnOlderQualifyingOne()
    {
        using var db = NewInMemoryDb();
        // Older row: would qualify on its own (viewed recently, past cooldown).
        db.GeneratedReports.Add(Report(TenantId, "tenant", "compliance_health", "FY2025-26",
            generatedAtUtc: Now.AddDays(-60), lastViewedUtc: Now.AddDays(-45)));
        // Newer row for the SAME key: generated too recently (within cooldown) - the whole key must be excluded.
        db.GeneratedReports.Add(Report(TenantId, "tenant", "compliance_health", "FY2025-26",
            generatedAtUtc: Now.AddDays(-5), lastViewedUtc: null));
        await db.SaveChangesAsync();

        var candidates = await PaidKeepWarmScheduler.GetKeepWarmCandidatesAsync(db, TenantId, ViewedSinceUtc, CooldownBeforeUtc, CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Candidates_NeverLeaksAnotherTenantsKey()
    {
        using var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report(customerId: 1490, "tenant", "compliance_health", "FY2025-26",
            generatedAtUtc: Now.AddDays(-40), lastViewedUtc: Now.AddDays(-10)));
        await db.SaveChangesAsync();

        var candidates = await PaidKeepWarmScheduler.GetKeepWarmCandidatesAsync(db, TenantId, ViewedSinceUtc, CooldownBeforeUtc, CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Candidates_TreatsDifferentScopesOnTheSameTenant_AsSeparateKeys()
    {
        using var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report(TenantId, "tenant", "compliance_health", "FY2025-26",
            generatedAtUtc: Now.AddDays(-40), lastViewedUtc: Now.AddDays(-10)));
        db.GeneratedReports.Add(Report(TenantId, "entity:100", "compliance_health", "FY2025-26",
            generatedAtUtc: Now.AddDays(-40), lastViewedUtc: Now.AddDays(-10)));
        await db.SaveChangesAsync();

        var candidates = await PaidKeepWarmScheduler.GetKeepWarmCandidatesAsync(db, TenantId, ViewedSinceUtc, CooldownBeforeUtc, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
    }

    [Theory]
    [InlineData(0, 28, 1)]
    [InlineData(27, 28, 28)]
    [InlineData(28, 28, 1)]
    [InlineData(-29, 28, 2)]
    public void AnchorDayOfMonthFor_IsDeterministic_AndAlwaysAValidCalendarDay(int customerId, int modulo, int expectedDay)
    {
        var day = PaidKeepWarmScheduler.AnchorDayOfMonthFor(customerId, modulo);

        Assert.Equal(expectedDay, day);
        Assert.InRange(day, 1, modulo);
    }

    [Fact]
    public void AnchorDayOfMonthFor_IsStableAcrossRepeatedCalls()
    {
        // [TRAP it guards against] String.GetHashCode is randomised per process - a tenant landing
        // on a different day after every restart would let a keep-warm cycle double-fire in one
        // month and skip the next. The id is already an integer; nothing here may vary by run.
        var first = PaidKeepWarmScheduler.AnchorDayOfMonthFor(29, 28);
        var second = PaidKeepWarmScheduler.AnchorDayOfMonthFor(29, 28);

        Assert.Equal(first, second);
    }
}
