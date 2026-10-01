using Insights.Data;
using Insights.Domain;
using Insights.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Insights.UnitTests;

/// <summary>
/// [ADDED 2026-09-25] The cooldown key is now (customerId, reportType, scopeDescriptor, dimension) -
/// Period dropped entirely, per the real product decision to stop letting a caller dodge the lock
/// for the SAME dimension by sending a different period string. See ICooldownRepository's own doc
/// comment for the full reasoning.
/// </summary>
public sealed class EfCooldownRepositoryTests
{
    private const int TenantId = 1285;
    private const string ReportType = "dimension_selection";
    private const string Scope = "branch:100,101";
    private const int CooldownDays = 30;

    private static InsightsReportsDbContext NewInMemoryDb() =>
        new(new DbContextOptionsBuilder<InsightsReportsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static GeneratedReport Report(
        string period, DateTime generatedAtUtc, string status = "complete", string reportType = ReportType,
        string? requestedDimensions = null) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = TenantId,
        ScopeDescriptor = Scope,
        ReportType = reportType,
        Period = period,
        RequestedDimensions = requestedDimensions,
        GeneratedAtUtc = generatedAtUtc,
        GeneratedByUserId = 38,
        BlobContainer = "insights-reports-temp",
        BlobPath = "abc123.dat",
        Status = status,
        EncryptedAesKey = [1, 2, 3],
        KeyVaultObjectName = "TRPLCryptoKey-001",
        KeyVaultObjectVersion = "https://vault.azure.net/keys/TRPLCryptoKey-001/abc123",
    };

    [Fact]
    public async Task CheckAsync_CompletedReportStoredUnderADifferentPeriodText_StillLocksOnDimension()
    {
        var db = NewInMemoryDb();
        // Stored with "last_30_days" as its period prefix - a caller now asking with a totally
        // different period choice ("last_90_days", a quarter, anything) for the SAME real
        // dimension must still be locked, because CheckAsync no longer looks at period text at
        // all - it receives and matches on the real dimension name directly. This is the whole
        // point of the redesign: the caller-supplied period never even reaches this check any more.
        db.GeneratedReports.Add(Report("last_30_days::dim=act", DateTime.UtcNow.AddDays(-5)));
        await db.SaveChangesAsync();

        var repo = new EfCooldownRepository(db, CooldownDays, cooldownEnabled: true);

        var result = await repo.CheckAsync(TenantId, ReportType, Scope, "act");

        Assert.False(result.IsOpen);
    }

    [Fact]
    public async Task CheckAsync_DifferentDimension_IsOpen()
    {
        var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report("last_30_days::dim=act", DateTime.UtcNow.AddDays(-5)));
        await db.SaveChangesAsync();

        var repo = new EfCooldownRepository(db, CooldownDays, cooldownEnabled: true);

        var result = await repo.CheckAsync(TenantId, ReportType, Scope, "event");

        Assert.True(result.IsOpen);
    }

    [Fact]
    public async Task CheckAsync_DimensionNameNormalisedCaseAndWhitespace_StillMatches()
    {
        var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report("q1::dim=backlogaging", DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        var repo = new EfCooldownRepository(db, CooldownDays, cooldownEnabled: true);

        var result = await repo.CheckAsync(TenantId, ReportType, Scope, "  BacklogAging  ");

        Assert.False(result.IsOpen);
    }

    [Fact]
    public async Task CheckAsync_NoCompletedReportForThisDimension_IsOpen()
    {
        var db = NewInMemoryDb();
        // A queued (not complete) row for the same dimension must never lock - "not consumed on failure".
        db.GeneratedReports.Add(Report("last_30_days::dim=act", DateTime.UtcNow, status: "queued"));
        await db.SaveChangesAsync();

        var repo = new EfCooldownRepository(db, CooldownDays, cooldownEnabled: true);

        var result = await repo.CheckAsync(TenantId, ReportType, Scope, "act");

        Assert.True(result.IsOpen);
    }

    [Fact]
    public async Task CheckAsync_NullDimension_LocksOnScopeAndReportTypeAlone()
    {
        var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report("FY2025-26", DateTime.UtcNow.AddDays(-2), reportType: "fixed_holistic"));
        await db.SaveChangesAsync();

        var repo = new EfCooldownRepository(db, CooldownDays, cooldownEnabled: true);

        var result = await repo.CheckAsync(TenantId, "fixed_holistic", Scope, dimension: null);

        Assert.False(result.IsOpen);
    }

    [Fact]
    public async Task CheckAsync_ReturnsRealDaysRemaining()
    {
        var db = NewInMemoryDb();
        // Generated 25 days ago, 30-day cooldown -> 5 real days left.
        db.GeneratedReports.Add(Report("last_30_days::dim=act", DateTime.UtcNow.AddDays(-25)));
        await db.SaveChangesAsync();

        var repo = new EfCooldownRepository(db, CooldownDays, cooldownEnabled: true);

        var result = await repo.CheckAsync(TenantId, ReportType, Scope, "act");

        Assert.False(result.IsOpen);
        Assert.NotNull(result.DaysRemaining);
        Assert.InRange(result.DaysRemaining!.Value, 4, 5);
    }

    [Fact]
    public async Task CheckAsync_CooldownDisabled_AlwaysOpenRegardlessOfCompletedReports()
    {
        var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report("last_30_days::dim=act", DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        var repo = new EfCooldownRepository(db, CooldownDays, cooldownEnabled: false);

        var result = await repo.CheckAsync(TenantId, ReportType, Scope, "act");

        Assert.True(result.IsOpen);
        Assert.Null(result.NextAvailableUtc);
        Assert.Null(result.DaysRemaining);
    }

    [Fact]
    public async Task CheckAsync_AgedOutCompletedReport_IsOpen()
    {
        var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report("last_30_days::dim=act", DateTime.UtcNow.AddDays(-31)));
        await db.SaveChangesAsync();

        var repo = new EfCooldownRepository(db, CooldownDays, cooldownEnabled: true);

        var result = await repo.CheckAsync(TenantId, ReportType, Scope, "act");

        Assert.True(result.IsOpen);
    }

    /// <summary>
    /// [ADDED 2026-09-25, sql/33 deployed] A row written by the updated PersistActivity carries
    /// the real RequestedDimensions column - the lookup must use it DIRECTLY, with no dependency on
    /// the legacy "::dim=" Period suffix at all (Period here is plain, no suffix).
    /// </summary>
    [Fact]
    public async Task CheckAsync_RealColumnPresent_LocksWithoutNeedingTheLegacyPeriodSuffix()
    {
        var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report("last_30_days", DateTime.UtcNow.AddDays(-1), requestedDimensions: "act"));
        await db.SaveChangesAsync();

        var repo = new EfCooldownRepository(db, CooldownDays, cooldownEnabled: true);

        var result = await repo.CheckAsync(TenantId, ReportType, Scope, "act");

        Assert.False(result.IsOpen);
    }

    /// <summary>
    /// The real column is a comma-joined LIST (sql/33's own CK_GeneratedReport_DimensionKeyAgrees
    /// constraint tests membership, not equality) - a row covering several dimensions must lock
    /// each one of them individually.
    /// </summary>
    [Fact]
    public async Task CheckAsync_RealColumnListsSeveralDimensions_MatchesEachByMembership()
    {
        var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report("last_30_days", DateTime.UtcNow.AddDays(-1), requestedDimensions: "act,nature"));
        await db.SaveChangesAsync();

        var repo = new EfCooldownRepository(db, CooldownDays, cooldownEnabled: true);

        var natureResult = await repo.CheckAsync(TenantId, ReportType, Scope, "nature");
        var internalResult = await repo.CheckAsync(TenantId, ReportType, Scope, "internal");

        Assert.False(natureResult.IsOpen);
        Assert.True(internalResult.IsOpen);
    }

    /// <summary>
    /// Legacy row (RequestedDimensions still NULL, written before sql/33 existed) must keep
    /// locking correctly off the old Period suffix during the transition.
    /// </summary>
    [Fact]
    public async Task CheckAsync_LegacyRowWithNoRequestedDimensionsColumn_StillFallsBackToPeriodSuffix()
    {
        var db = NewInMemoryDb();
        db.GeneratedReports.Add(Report("last_30_days::dim=risk", DateTime.UtcNow.AddDays(-1), requestedDimensions: null));
        await db.SaveChangesAsync();

        var repo = new EfCooldownRepository(db, CooldownDays, cooldownEnabled: true);

        var result = await repo.CheckAsync(TenantId, ReportType, Scope, "risk");

        Assert.False(result.IsOpen);
    }
}
