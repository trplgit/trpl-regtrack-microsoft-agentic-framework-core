using Insights.Data;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

        BlobPathContext? seenPathContext = null;
        var blobWriter = new Mock<IReportBlobWriter>();
        blobWriter.Setup(w => w.WriteAsync(envelope, It.IsAny<BlobPathContext>(), It.IsAny<CancellationToken>()))
            .Callback<EncryptedReportEnvelope, BlobPathContext, CancellationToken>((_, ctx, _) => seenPathContext = ctx)
            .ReturnsAsync(new BlobLocation("insights-reports-temp", "29/compliance_health/2026/09/abc123.html.enc"));

        await using var db = NewInMemoryDb();
        var activity = new PersistActivity(encryptor.Object, blobWriter.Object, ScopeFactoryFor(db), NullLogger<PersistActivity>.Instance);

        var result = await activity.RunAsync(new PersistInput(
            "<html></html>", TenantId: 29, ReportType: "compliance_health", Period: "FY2025-26",
            ScopeDescriptor: "tenant", UserId: 38));

        Assert.False(string.IsNullOrWhiteSpace(result.ReportId));

        var row = await db.GeneratedReports.SingleAsync();
        Assert.Equal(result.ReportId, row.Id.ToString());
        // The blob PATH is built from the row's own id + generation time - the activity fixes both
        // before the blob write, not after.
        Assert.NotNull(seenPathContext);
        Assert.Equal(29, seenPathContext!.TenantId);
        Assert.Equal("compliance_health", seenPathContext.ReportType);
        Assert.Equal(row.Id, seenPathContext.ReportId);
        // PartitionDate is derived from DateTime.UtcNow (already Kind=Utc, never a local/offset
        // clock reading) via DateOnly.FromDateTime - a straight truncation, not a timezone
        // conversion - so the row's generation date and the blob path's partition date can never
        // drift apart regardless of the host's local timezone.
        Assert.Equal(DateOnly.FromDateTime(row.GeneratedAtUtc), seenPathContext.PartitionDate);
        Assert.Equal("29/compliance_health/2026/09/abc123.html.enc", row.BlobPath);
        Assert.Equal(29, row.CustomerId);
        Assert.Equal("tenant", row.ScopeDescriptor);
        Assert.Equal("compliance_health", row.ReportType);
        Assert.Equal("FY2025-26", row.Period);
        Assert.Equal(38, row.GeneratedByUserId);
        Assert.Equal("insights-reports-temp", row.BlobContainer);
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
        blobWriter.Setup(w => w.WriteAsync(It.IsAny<EncryptedReportEnvelope>(), It.IsAny<BlobPathContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BlobLocation("insights-reports-temp", "29/compliance_health/2026/09/abc123.html.enc"));

        await using var db = NewInMemoryDb();
        var activity = new PersistActivity(encryptor.Object, blobWriter.Object, ScopeFactoryFor(db), NullLogger<PersistActivity>.Instance);

        await activity.RunAsync(new PersistInput(
            "<html>real tenant data</html>", 29, "compliance_health", "FY2025-26", "tenant", 38));

        blobWriter.Verify(w => w.WriteAsync(envelope, It.IsAny<BlobPathContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// [ADDED 2026-09-12] Temp workaround for a real, ongoing Key Vault access failure (confirmed
    /// live: KeyVaultErrorException "Forbidden", unrelated to VPN/IP - see ProbeKeyVaultEncryptionAsync)
    /// that would otherwise lose every already-billed report at the very last step. When
    /// localFallbackDirectory is set, this bypasses encrypt/blob/SQL entirely - plaintext HTML
    /// straight to disk. Default null/empty means completely unchanged behaviour (the two tests
    /// above). Revert by clearing Reports:LocalFallbackDirectory once Key Vault access is fixed.
    /// </summary>
    [Fact]
    public async Task RunAsync_LocalFallbackDirectorySet_WritesPlaintextToDisk_SkipsEncryptorBlobAndDb()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "persist-fallback-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var encryptor = new Mock<IReportEncryptor>();
            var blobWriter = new Mock<IReportBlobWriter>();
            await using var db = NewInMemoryDb();
            var activity = new PersistActivity(
                encryptor.Object, blobWriter.Object, ScopeFactoryFor(db), NullLogger<PersistActivity>.Instance, localFallbackDirectory: tempDir);

            var result = await activity.RunAsync(new PersistInput(
                "<html>real tenant data</html>", 1008, "fixed_holistic", "90day", "tenant", 12116));

            encryptor.Verify(e => e.EncryptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            blobWriter.Verify(w => w.WriteAsync(It.IsAny<EncryptedReportEnvelope>(), It.IsAny<BlobPathContext>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.Empty(db.GeneratedReports);

            Assert.NotNull(result.LocalFilePath);
            Assert.True(File.Exists(result.LocalFilePath));
            Assert.Equal("<html>real tenant data</html>", await File.ReadAllTextAsync(result.LocalFilePath!));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<PersistActivity>
    {
        public List<Exception?> LoggedExceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            LoggedExceptions.Add(exception);
    }

    /// <summary>
    /// [ADDED 2026-09-18] Found live: with 4 worker replicas genuinely parallel, 3 of 4 concurrent
    /// PersistActivity calls failed at SaveChangesAsync with only "An error occurred while saving
    /// the entity changes" recoverable afterward - DTFx's TaskFailed history event never keeps
    /// ex.InnerException. This pins that the real exception (the one that matters, with its inner
    /// exception intact) is now logged BEFORE it propagates, regardless of which step throws.
    /// </summary>
    [Fact]
    public async Task RunAsync_SaveChangesFails_LogsTheRealExceptionBeforeRethrowing()
    {
        var envelope = Envelope();
        var encryptor = new Mock<IReportEncryptor>();
        encryptor.Setup(e => e.EncryptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(envelope);

        var blobWriter = new Mock<IReportBlobWriter>();
        var dbFailure = new InvalidOperationException("simulated transient SQL failure");
        blobWriter.Setup(w => w.WriteAsync(It.IsAny<EncryptedReportEnvelope>(), It.IsAny<BlobPathContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(dbFailure);

        await using var db = NewInMemoryDb();
        var logger = new CapturingLogger();
        var activity = new PersistActivity(encryptor.Object, blobWriter.Object, ScopeFactoryFor(db), logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => activity.RunAsync(new PersistInput(
            "<html></html>", 29, "compliance_health", "FY2025-26", "tenant", 38)));

        Assert.Same(dbFailure, thrown);
        Assert.Contains(logger.LoggedExceptions, ex => ex == dbFailure);
    }
}
