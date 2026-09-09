using Insights.Data;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Insights.UnitTests;

public class PersistActivityTests
{
    private static InsightsReportsDbContext NewInMemoryDb() =>
        new(new DbContextOptionsBuilder<InsightsReportsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// PersistActivity now takes IServiceScopeFactory rather than InsightsReportsDbContext
    /// directly (fixes the "Cannot access a disposed context instance" bug where ActivityCreator's
    /// own scope disposed the context before RunAsync could use it). The fake factory hands back
    /// the SAME db instance every time, matching a real scoped registration closely enough for
    /// these tests, which only ever create one scope per RunAsync call.
    /// </summary>
    private static IServiceScopeFactory ScopeFactoryFor(InsightsReportsDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static EncryptedReportEnvelope Envelope() =>
        new(
            Content: [1, 2, 3],
            EncryptedAesKey: [4, 5, 6],
            KeyVaultObjectName: "TRPLCryptoKey-001",
            KeyVaultObjectVersion: "https://vault.azure.net/keys/TRPLCryptoKey-001/abc123");

    [Fact]
    public async Task RunAsync_EncryptsWritesAndIndexes_ReturnsTheRowId()
    {
        var envelope = Envelope();
        var encryptor = new Mock<IReportEncryptor>();
        encryptor.Setup(e => e.EncryptAsync("<html></html>", It.IsAny<CancellationToken>())).ReturnsAsync(envelope);

        var blobWriter = new Mock<IReportBlobWriter>();
        blobWriter.Setup(w => w.WriteAsync(envelope, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlobLocation("insights-reports-temp", "abc123.dat"));

        await using var db = NewInMemoryDb();
        var activity = new PersistActivity(encryptor.Object, blobWriter.Object, ScopeFactoryFor(db));

        var result = await activity.RunAsync(new PersistInput(
            "<html></html>", TenantId: 29, ReportType: "compliance_health", Period: "FY2025-26",
            ScopeDescriptor: "tenant", UserId: 38));

        Assert.False(string.IsNullOrWhiteSpace(result.ReportId));

        var row = await db.GeneratedReports.SingleAsync();
        Assert.Equal(result.ReportId, row.Id.ToString());
        Assert.Equal(29, row.CustomerId);
        Assert.Equal("tenant", row.ScopeDescriptor);
        Assert.Equal("compliance_health", row.ReportType);
        Assert.Equal("FY2025-26", row.Period);
        Assert.Equal(38, row.GeneratedByUserId);
        Assert.Equal("insights-reports-temp", row.BlobContainer);
        Assert.Equal("abc123.dat", row.BlobPath);
        Assert.Equal("complete", row.Status);
        Assert.Equal(envelope.EncryptedAesKey, row.EncryptedAesKey);
        Assert.Equal("TRPLCryptoKey-001", row.KeyVaultObjectName);
        Assert.Equal(envelope.KeyVaultObjectVersion, row.KeyVaultObjectVersion);
        Assert.Equal("0", row.KeyVaultObjectSalt);
    }

    /// <summary>The plaintext HTML must never reach the blob writer directly - only the encryptor's output does.</summary>
    [Fact]
    public async Task RunAsync_NeverPassesPlaintextToTheBlobWriter()
    {
        var envelope = Envelope();
        var encryptor = new Mock<IReportEncryptor>();
        encryptor.Setup(e => e.EncryptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(envelope);

        var blobWriter = new Mock<IReportBlobWriter>();
        blobWriter.Setup(w => w.WriteAsync(It.IsAny<EncryptedReportEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlobLocation("insights-reports-temp", "abc123.dat"));

        await using var db = NewInMemoryDb();
        var activity = new PersistActivity(encryptor.Object, blobWriter.Object, ScopeFactoryFor(db));

        await activity.RunAsync(new PersistInput(
            "<html>real tenant data</html>", 29, "compliance_health", "FY2025-26", "tenant", 38));

        blobWriter.Verify(w => w.WriteAsync(envelope, It.IsAny<CancellationToken>()), Times.Once);
    }
}
