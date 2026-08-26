using Insights.Data;
using Insights.Domain;
using Insights.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Insights.UnitTests;

/// <summary>
/// Item 14's read half (design doc Sec.9.3, API_CONTRACTS.md §5). The one thing worth pinning hard
/// here: report_scope subset-of viewer_scope is re-checked against CURRENT scope every call, never
/// the scope that existed when the report was generated - that is the entire point of view-time
/// re-authorisation (closes pre-mortem D4).
/// </summary>
public sealed class ReportContentServiceTests
{
    private const int TenantId = 29;
    private const int ViewerUserId = 38;

    private static InsightsReportsDbContext NewInMemoryDb() =>
        new(new DbContextOptionsBuilder<InsightsReportsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static GeneratedReport Report(string scopeDescriptor) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = TenantId,
        ScopeDescriptor = scopeDescriptor,
        ReportType = "compliance_health",
        Period = "FY2025-26",
        GeneratedByUserId = ViewerUserId,
        BlobContainer = "insights-reports-temp",
        BlobPath = "abc123.dat",
        Status = "complete",
        EncryptedAesKey = [1, 2, 3],
        KeyVaultObjectName = "TRPLCryptoKey-001",
        KeyVaultObjectVersion = "https://vault.azure.net/keys/TRPLCryptoKey-001/abc123",
    };

    /// <summary>Apex 100 -> child 101 -> grandchild 102. A subtree check against entity 100 must require ALL THREE.</summary>
    private static IReadOnlyList<EntityTreeNode> ThreeNodeSubtree() =>
    [
        new(100, "Apex", null, 100, "Apex", EntityRootKind.Apex, 0, EntityNodeType.Intermediate),
        new(101, "Child", 100, 100, "Apex", EntityRootKind.Apex, 1, EntityNodeType.Intermediate),
        new(102, "Grandchild", 101, 100, "Apex", EntityRootKind.Apex, 2, EntityNodeType.Leaf),
    ];

    private static (ReportContentService Service, InsightsReportsDbContext Db, Mock<IReportViewPublisher> Publisher) BuildService(
        IReadOnlyList<ScopePair> viewerPairs, IReadOnlyList<EntityTreeNode>? tree = null)
    {
        var db = NewInMemoryDb();

        var scope = new Mock<IScopeRepository>();
        scope.Setup(s => s.GetScopePairsAsync(ViewerUserId, TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(viewerPairs);

        var entities = new Mock<IEntityRepository>();
        entities.Setup(e => e.GetEntityTreeAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(tree ?? []);

        var decryptor = new Mock<IReportDecryptor>();
        decryptor.Setup(d => d.DecryptAsync(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html>decrypted</html>");

        var blobReader = new Mock<IReportBlobReader>();
        blobReader.Setup(r => r.ReadAsync(It.IsAny<BlobLocation>(), It.IsAny<CancellationToken>())).ReturnsAsync([1, 2, 3]);

        var publisher = new Mock<IReportViewPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReportViewLocation(new Uri("https://blob.example/views/abc.html?sv=sig"), DateTimeOffset.UtcNow.AddMinutes(10)));

        var service = new ReportContentService(
            db, scope.Object, entities.Object, decryptor.Object, blobReader.Object, publisher.Object,
            TimeSpan.FromMinutes(10), NullLogger<ReportContentService>.Instance);

        return (service, db, publisher);
    }

    [Fact]
    public async Task ReturnsNull_WhenNoSuchReportExistsOnTheTenant()
    {
        var (service, db, _) = BuildService(viewerPairs: [new ScopePair(1, 1)]);
        db.GeneratedReports.Add(Report("tenant"));
        await db.SaveChangesAsync();

        var result = await service.OpenAsync(Guid.NewGuid(), TenantId, ViewerUserId);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenTheReportBelongsToAnotherTenant()
    {
        var (service, db, _) = BuildService(viewerPairs: [new ScopePair(1, 1)]);
        var report = Report("tenant");
        db.GeneratedReports.Add(report);
        await db.SaveChangesAsync();

        var result = await service.OpenAsync(report.Id, tenantId: 9999, ViewerUserId);

        Assert.Null(result);
    }

    /// <summary>THE ONE THAT MATTERS. Scope fully revoked since generation -> the report vanishes. Closes D4.</summary>
    [Fact]
    public async Task ReturnsNull_ForATenantScopedReport_WhenTheViewersCurrentScopeIsEmpty()
    {
        var (service, db, _) = BuildService(viewerPairs: []);
        var report = Report("tenant");
        db.GeneratedReports.Add(report);
        await db.SaveChangesAsync();

        var result = await service.OpenAsync(report.Id, TenantId, ViewerUserId);

        Assert.Null(result);
    }

    [Fact]
    public async Task Succeeds_ForATenantScopedReport_WhenTheViewerStillHasAnyScope()
    {
        var (service, db, _) = BuildService(viewerPairs: [new ScopePair(1, 1)]);
        var report = Report("tenant");
        db.GeneratedReports.Add(report);
        await db.SaveChangesAsync();

        var result = await service.OpenAsync(report.Id, TenantId, ViewerUserId);

        Assert.NotNull(result);
        Assert.True(result.SandboxRequired);
    }

    /// <summary>Viewer's current branches cover the whole subtree the report was scoped to - passes.</summary>
    [Fact]
    public async Task Succeeds_ForAnEntityScopedReport_WhenTheViewerCurrentlyCoversTheWholeSubtree()
    {
        var viewerPairs = new[] { 100, 101, 102 }.Select(b => new ScopePair(b, 1)).ToArray();
        var (service, db, _) = BuildService(viewerPairs, ThreeNodeSubtree());
        var report = Report("entity:100");
        db.GeneratedReports.Add(report);
        await db.SaveChangesAsync();

        var result = await service.OpenAsync(report.Id, TenantId, ViewerUserId);

        Assert.NotNull(result);
    }

    /// <summary>
    /// [TRAP - the precise case §9.3's re-auth exists for] Viewer's category access narrowed since
    /// generation is NOT modelled here (ScopeDescriptor has no category info - see the class doc
    /// comment's known limitation) but a BRANCH being dropped from scope IS precisely checkable,
    /// and must refuse.
    /// </summary>
    [Fact]
    public async Task ReturnsNull_ForAnEntityScopedReport_WhenTheViewerNoLongerCoversPartOfTheSubtree()
    {
        // Lost branch 102 (the grandchild) since generation - only 100 and 101 remain.
        var viewerPairs = new[] { 100, 101 }.Select(b => new ScopePair(b, 1)).ToArray();
        var (service, db, _) = BuildService(viewerPairs, ThreeNodeSubtree());
        var report = Report("entity:100");
        db.GeneratedReports.Add(report);
        await db.SaveChangesAsync();

        var result = await service.OpenAsync(report.Id, TenantId, ViewerUserId);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_ForAnEntityScopedReport_WhenTheEntityNoLongerExistsInTheTree()
    {
        var viewerPairs = new[] { 100 }.Select(b => new ScopePair(b, 1)).ToArray();
        var (service, db, _) = BuildService(viewerPairs, tree: []); // entity 100 deleted/gone entirely
        var report = Report("entity:100");
        db.GeneratedReports.Add(report);
        await db.SaveChangesAsync();

        var result = await service.OpenAsync(report.Id, TenantId, ViewerUserId);

        Assert.Null(result);
    }

    /// <summary>The permanent encrypted blob's own SAS is never minted - only a freshly-decrypted copy gets one.</summary>
    [Fact]
    public async Task PublishesTheDecryptedContent_NeverThePermanentEncryptedBlob()
    {
        var (service, db, publisher) = BuildService(viewerPairs: [new ScopePair(1, 1)]);
        var report = Report("tenant");
        db.GeneratedReports.Add(report);
        await db.SaveChangesAsync();

        await service.OpenAsync(report.Id, TenantId, ViewerUserId);

        publisher.Verify(p => p.PublishAsync("<html>decrypted</html>", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>sql/19 - the paid keep-warm scheduler's sole signal that a report was actually opened (design doc Sec.4.3).</summary>
    [Fact]
    public async Task StampsLastViewedUtc_OnASuccessfulView()
    {
        var (service, db, _) = BuildService(viewerPairs: [new ScopePair(1, 1)]);
        var report = Report("tenant");
        db.GeneratedReports.Add(report);
        await db.SaveChangesAsync();
        Assert.Null(report.LastViewedUtc);

        var before = DateTime.UtcNow;
        await service.OpenAsync(report.Id, TenantId, ViewerUserId);
        var after = DateTime.UtcNow;

        var stored = await db.GeneratedReports.FindAsync(report.Id);
        Assert.NotNull(stored!.LastViewedUtc);
        Assert.InRange(stored.LastViewedUtc!.Value, before, after);
    }

    /// <summary>A refused view (scope no longer covers the report) must never look like a real one to the keep-warm scheduler.</summary>
    [Fact]
    public async Task DoesNotStampLastViewedUtc_WhenTheViewIsRefused()
    {
        var (service, db, _) = BuildService(viewerPairs: []);
        var report = Report("tenant");
        db.GeneratedReports.Add(report);
        await db.SaveChangesAsync();

        var result = await service.OpenAsync(report.Id, TenantId, ViewerUserId);

        Assert.Null(result);
        var stored = await db.GeneratedReports.FindAsync(report.Id);
        Assert.Null(stored!.LastViewedUtc);
    }
}
