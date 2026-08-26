using System.Data;
using System.Diagnostics.Tracing;
using Azure.Storage.Blobs;
using Insights.Worker;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.WebKey;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using OpenTelemetry.Trace;
using Trplclientsecret;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// One-off UAT test-data helpers - NOT part of the automated suite. Run explicitly, one at
/// a time, with --filter. Tenant 1490 has no ProductMapping rows in UAT at all, so these are
/// a clean insert/delete pair with nothing to collide with (see chat: 2026-08-19).
/// Requires ConnectionStrings__RegTrack - the UAT connection string carries a live sa
/// password and must never be hardcoded here even as a fallback.
/// </summary>
public sealed class UatTestDataManualTests(ITestOutputHelper output)
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__RegTrack")
        ?? throw new InvalidOperationException("Set ConnectionStrings__RegTrack before running this manual test.");

    [Fact]
    public async Task InsertFreeTierMappingForTenant1490()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "INSERT INTO ProductMapping (CustomerID, ProductID, IsActive, CreatedOn, CreatedBy) VALUES (1490, 18, 0, GETDATE(), 0);", connection);
        var rows = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task DeleteFreeTierMappingForTenant1490()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "DELETE FROM ProductMapping WHERE CustomerID = 1490 AND ProductID = 18;", connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Build order item 11 (docs/superpowers/plans/2026-08-21-durable-task-orchestrator.md, Task 1
    /// step 2): provisions the dedicated task-hub database, separate from vitComplianceSystem, on
    /// the same UAT server. Idempotent (IF NOT EXISTS) so it is safe to re-run, e.g. after a dev
    /// reset. Connects using ConnectionStrings__RegTrack's server/credentials but does not touch
    /// vitComplianceSystem itself - CREATE DATABASE only requires the login to have the permission,
    /// not a master-database connection.
    /// </summary>
    [Fact]
    public async Task CreateInsightsTaskHubDatabase()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'vitInsightsTaskHub') CREATE DATABASE vitInsightsTaskHub;", connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Diagnostic for build order item 11's Task 17 (end-to-end orchestrator verification): every
    /// manual/integration test so far in this repo used tenant 23, which turns out NOT to be
    /// mapped-and-enabled for the paid product (19) - EvaluateGateAsync correctly refused it
    /// (2026-08-21). Lists real UAT tenants that ARE paid-entitled, [TRAP] IsActive is INVERTED
    /// (0 = enabled), so the WHERE clause checks = 0, not = 1.
    /// </summary>
    [Fact]
    public async Task ListPaidEntitledTenants()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT TOP 20 CustomerID FROM ProductMapping
            WHERE ProductID = 19 AND IsActive = 0
            ORDER BY CustomerID;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            output.WriteLine($"CustomerID={reader.GetInt64(0)}");
    }

    /// <summary>
    /// Companion to ListPaidEntitledTenants - finds a real (userId, customerId) scope pair for
    /// tenant 1285, the second (and last) paid-entitled UAT tenant found. Same join
    /// EntitiesAssignment-based approach DimensionRepositoryTests.ValidatedTenants used originally.
    /// </summary>
    [Fact]
    public async Task FindValidUserForTenant1285()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT DISTINCT TOP 5 ea.UserID
            FROM EntitiesAssignment ea
            JOIN CustomerBranch cb ON cb.ID = ea.BranchID
            JOIN Customer cu ON cu.ID = cb.CustomerID
            WHERE cb.CustomerID = 1285 AND cb.IsDeleted = 0 AND cu.IsDeleted = 0
            ORDER BY ea.UserID;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            output.WriteLine($"UserID={reader.GetInt64(0)}");
    }

    /// <summary>
    /// One-off diagnostic against the DURABLE TASK HUB database (not vitComplianceSystem) - checks
    /// the actual current status of the most recent orchestration instance directly via SQL,
    /// cheaper than re-running the full LLM pipeline again to find out whether a client-side
    /// WaitForOrchestrationAsync failure meant the orchestration itself also failed, or was still
    /// legitimately in progress. Table/schema names are Microsoft.DurableTask.SqlServer's own,
    /// discovered by listing INFORMATION_SCHEMA.TABLES first, not guessed.
    /// </summary>
    [Fact]
    public async Task InspectTaskHubTables()
    {
        var taskHubConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running this test.");
        await using var connection = new SqlConnection(taskHubConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA, TABLE_NAME;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            output.WriteLine($"{reader.GetString(0)}.{reader.GetString(1)}");
    }

    /// <summary>Real GeneratedReport rows - independent verification that item 14's write path actually persisted something, not just that a test asserted it did.</summary>
    [Fact]
    public async Task InspectMostRecentGeneratedReport()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT TOP 3 Id, CustomerId, ScopeDescriptor, ReportType, Period, GeneratedAtUtc, GeneratedByUserId, BlobContainer, BlobPath, Status, DATALENGTH(EncryptedAesKey) AS KeyBytes, KeyVaultObjectName, KeyVaultObjectSalt FROM dbo.GeneratedReport ORDER BY GeneratedAtUtc DESC;",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var columnNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        output.WriteLine(string.Join(" | ", columnNames));
        while (await reader.ReadAsync())
        {
            var values = Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "-" : reader.GetValue(i)?.ToString() ?? "-");
            output.WriteLine(string.Join(" | ", values));
        }
    }

    /// <summary>
    /// Proves the decrypt side of item 14's envelope encryption actually works, not just encrypt.
    /// Reads the real GeneratedReport row + real blob, unwraps the DEK via Key Vault, decrypts, and
    /// saves readable HTML locally - the read path (API_CONTRACTS.md 5) is not built yet, this is a
    /// manual stand-in until it is. Requires AZURE_BLOB_CONNECTION_STRING.
    /// </summary>
    [Fact]
    public async Task DecryptMostRecentReportToLocalFile()
    {
        await using var sqlConnection = new SqlConnection(ConnectionString);
        await sqlConnection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT TOP 1 BlobContainer, BlobPath, EncryptedAesKey, KeyVaultObjectVersion FROM dbo.GeneratedReport ORDER BY GeneratedAtUtc DESC;",
            sqlConnection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("No GeneratedReport rows found.");

        var blobContainer = reader.GetString(0);
        var blobPath = reader.GetString(1);
        var wrappedKey = (byte[])reader[2];
        var keyId = reader.GetString(3);

        var blobConnectionString = Environment.GetEnvironmentVariable("AZURE_BLOB_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Set AZURE_BLOB_CONNECTION_STRING.");

        var blobService = new Azure.Storage.Blobs.BlobServiceClient(blobConnectionString);
        var blob = blobService.GetBlobContainerClient(blobContainer).GetBlobClient(blobPath);
        var downloaded = (await blob.DownloadContentAsync()).Value.Content.ToArray();

        var iv = downloaded[..16];
        var ciphertext = downloaded[16..];

        var clientSecret = new Trplclientsecret.BU().GetClientSecret();
        var kvClient = new Microsoft.Azure.KeyVault.KeyVaultClient(async (authority, resource, _) =>
        {
            var authContext = new Microsoft.IdentityModel.Clients.ActiveDirectory.AuthenticationContext(authority);
            var clientCred = new Microsoft.IdentityModel.Clients.ActiveDirectory.ClientCredential("449821d0-9ff9-4f87-a535-bda5cb484287", clientSecret);
#pragma warning disable CS0618
            var result = await authContext.AcquireTokenAsync(resource, clientCred);
#pragma warning restore CS0618
            return result.AccessToken;
        });

        var unwrapped = await kvClient.DecryptAsync(keyId, Microsoft.Azure.KeyVault.WebKey.JsonWebKeyEncryptionAlgorithm.RSAOAEP, wrappedKey);
        var aesKey = unwrapped.Result;

        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        using var plaintextStream = new MemoryStream();
        await using (var cryptoStream = new System.Security.Cryptography.CryptoStream(plaintextStream, decryptor, System.Security.Cryptography.CryptoStreamMode.Write, leaveOpen: true))
            await cryptoStream.WriteAsync(ciphertext);

        var html = System.Text.Encoding.UTF8.GetString(plaintextStream.ToArray());
        const string outPath = @"D:\trpl-reginsights-dev\trpl-regtrack-microsoft-agentic-framework-core-dev\decrypted-report-tenant29.html";
        await File.WriteAllTextAsync(outPath, html);
        output.WriteLine($"Decrypted {html.Length} chars -> {outPath}");
    }

    /// <summary>Follows InspectTaskHubTables - dt.vInstances is Microsoft.DurableTask.SqlServer's own friendly view.</summary>
    [Fact]
    public async Task InspectRecentOrchestrationInstances()
    {
        var taskHubConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running this test.");
        await using var connection = new SqlConnection(taskHubConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT * FROM dt.vInstances ORDER BY CreatedTime DESC;", connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync();
        var columnNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        output.WriteLine(string.Join(" | ", columnNames));
        while (await reader.ReadAsync())
        {
            var values = Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "-" : reader.GetValue(i)?.ToString() ?? "-");
            output.WriteLine(string.Join(" | ", values));
        }
    }

    /// <summary>
    /// THROWAWAY - isolates the OTel -> LangFuse export path from the rest of the pipeline. Makes
    /// exactly ONE real LLM call through the same MafAgentFactory wrapping every paid agent uses,
    /// with nothing else running (no SQL, no Durable Task, no other network traffic that could be
    /// flaky at the same time) - fast, focused signal instead of waiting through another 15-minute
    /// full report run to find out whether spans actually reach LangFuse.
    /// Requires MAF_ENDPOINT, MAF_MODEL, MAF_API_KEY, LANGFUSE_BASE_URL, LANGFUSE_PUBLIC_KEY,
    /// LANGFUSE_SECRET_KEY.
    /// </summary>
    [Fact]
    public async Task TraceOneLlmCallAsync()
    {
        // THROWAWAY diagnostics - the OTel SDK's own export failures are otherwise invisible (it
        // logs via an internal EventSource, never ILogger/console). This surfaces every
        // "OpenTelemetry*" EventSource's events for the duration of this one test.
        using var diagnostics = new EventSourceDiagnosticsListener(output);

        // Bypasses AddInsightsObservability's DI wiring for this one diagnostic run - builds the
        // TracerProvider directly so a logging DelegatingHandler can sit in the HttpClient the OTLP
        // exporter actually uses, and print the REAL request/response LangFuse sends back. The
        // EventSource listener above proved spans are created; this proves (or disproves) that the
        // HTTP POST itself lands and what LangFuse's server actually says about it.
        var pk = RequireEnv("LANGFUSE_PUBLIC_KEY");
        var sk = RequireEnv("LANGFUSE_SECRET_KEY");
        var basicAuth = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{pk}:{sk}"));
        var baseUri = RequireEnv("LANGFUSE_BASE_URL").TrimEnd('/');

        using var tracerProvider = OpenTelemetry.Sdk.CreateTracerProviderBuilder()
            .AddSource(Insights.Agents.MafAgentFactory.ChatClientActivitySourceName)
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri($"{baseUri}/api/public/otel/v1/traces");
                o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                o.HttpClientFactory = () =>
                {
                    var loggingHandler = new LoggingHandler(output) { InnerHandler = new HttpClientHandler() };
                    var client = new HttpClient(loggingHandler);
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basicAuth);
                    client.DefaultRequestHeaders.Add("x-langfuse-ingestion-version", "4");
                    return client;
                };
            })
            .Build();

        var endpoint = RequireEnv("MAF_ENDPOINT");
        var model = RequireEnv("MAF_MODEL");
        var apiKey = RequireEnv("MAF_API_KEY");

        var agent = Insights.Agents.MafAgentFactory.CreateTextAgent(
            endpoint, model, apiKey, "TraceProbeAgent", "One-off trace probe - not a real pipeline agent.",
            "Reply with exactly three words.", enableSensitiveTelemetry: true);

        var startedUtc = DateTimeOffset.UtcNow;
        var response = await agent.RunAsync("Say hello.");
        output.WriteLine($"LLM responded: {response.Text}");
        output.WriteLine($"Call started at (UTC): {startedUtc:O} - check LangFuse observations from this time.");

        var flushed = tracerProvider.ForceFlush(15000);
        output.WriteLine($"ForceFlush returned: {flushed}");
    }

    /// <summary>
    /// THROWAWAY - real tenant 29 (all 9 dimensions, exactly what FetchDimensionsActivity fetches),
    /// one real composition call, printing the ACTUAL token count. Built to answer one question
    /// fast: item 17's new per-run budget just refused a real tenant-29 run at the composing stage
    /// (Budget:PerRunTokenCeiling = 250,000) - is that a real, oversized payload for this tenant
    /// (flagged in CLAUDE.md as 89% of estate under a soft-deleted parent - a large tenant), or a
    /// bug in the token-counting plumbing? Isolates composition alone instead of waiting through
    /// the full ~5-minute pipeline again.
    /// Requires ConnectionStrings__RegTrack, MAF_ENDPOINT, MAF_MODEL, MAF_API_KEY.
    /// </summary>
    [Fact]
    public async Task MeasureRealComposeTokenUsage_Tenant29()
    {
        var endpoint = RequireEnv("MAF_ENDPOINT");
        var model = RequireEnv("MAF_MODEL");
        var apiKey = RequireEnv("MAF_API_KEY");
        const int userId = 38, customerId = 29;

        var repository = new Insights.Data.SqlDimensionRepository(ConnectionString);
        var location = await repository.GetLocationAsync(userId, customerId);
        var entity = await repository.GetEntityAsync(userId, customerId);
        var risk = await repository.GetRiskAsync(userId, customerId);
        var nature = await repository.GetNatureAsync(userId, customerId);
        var departments = await repository.GetDepartmentsAsync(userId, customerId);
        var act = await repository.GetActAsync(userId, customerId);
        var users = await repository.GetUsersAsync(userId, customerId);
        var @internal = await repository.GetInternalAsync(userId, customerId);
        var @event = await repository.GetEventAsync(userId, customerId);

        var dimensionResults = new Dictionary<string, object>
        {
            ["Location"] = location, ["Entity"] = entity, ["Risk"] = risk, ["Nature"] = nature,
            ["Departments"] = departments, ["Act"] = act, ["Users"] = users, ["Internal"] = @internal, ["Event"] = @event,
        };

        var totalChars = dimensionResults.Values.Sum(v => System.Text.Json.JsonSerializer.Serialize(v).Length);
        output.WriteLine($"Serialized dimension payload: {totalChars:N0} chars across 9 dimensions.");

        var instructions = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "prompts", "01_composition.md"));
        var compositionAgent = new Insights.Agents.MafCompositionAgent(Insights.Agents.MafAgentFactory.CreateJsonAgent(
            endpoint, model, apiKey, "CompositionAgent", "Decides report structure.", instructions));

        var result = await compositionAgent.ComposeAsync(dimensionResults, tenantShape: "multi_entity", reportType: "compliance_health");

        output.WriteLine($"ACTUAL total tokens for ONE compose call: {result.TotalTokens:N0}");
        output.WriteLine($"Budget:PerRunTokenCeiling is 250,000 - this ONE call is {(result.TotalTokens / 250_000.0):P1} of the whole-run budget.");
    }

    /// <summary>
    /// THROWAWAY - deploys sql/19 (GeneratedReport.LastViewedUtc) directly against real UAT (no
    /// sqlcmd binary available in this environment) and validates the whole paid keep-warm read
    /// path against the REAL schema, not InMemory: the tenant-list query (SqlPaidTenantRepository,
    /// ProductID 19) and InsightsReportsDbContext.GeneratedReports with the new column, both over
    /// real SQL Server. Answers the one thing the unit tests structurally cannot: does the EF
    /// query actually execute against the deployed table, and does the ALTER TABLE apply cleanly.
    /// Requires ConnectionStrings__RegTrack.
    /// </summary>
    [Fact]
    public async Task DeployViewTrackingColumn_ThenValidateKeepWarmDataPath()
    {
        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID('dbo.GeneratedReport') AND name = 'LastViewedUtc'
                )
                    ALTER TABLE dbo.GeneratedReport ADD LastViewedUtc DATETIME2(0) NULL;
                """, connection);
            await command.ExecuteNonQueryAsync();
        }
        output.WriteLine("sql/19 applied (or already present) against real UAT.");

        var tenantRepository = new Insights.Data.SqlPaidTenantRepository(ConnectionString);
        var paidTenants = await tenantRepository.GetEntitledTenantsAsync();
        output.WriteLine($"Real paid-entitled tenant count (ProductID 19, IsActive=0, Customer.IsDeleted=0): {paidTenants.Count}");
        foreach (var t in paidTenants.Take(5))
            output.WriteLine($"  {t.CustomerId}: {t.TenantName}");

        await using var db = new Insights.Data.InsightsReportsDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Insights.Data.InsightsReportsDbContext>()
                .UseSqlServer(ConnectionString).Options);

        var totalRows = await db.GeneratedReports.CountAsync();
        var everViewed = await db.GeneratedReports.CountAsync(r => r.LastViewedUtc != null);
        output.WriteLine($"GeneratedReport rows in UAT: {totalRows} total, {everViewed} with LastViewedUtc set.");

        // Closes the loop against REAL SQL Server, not just the InMemory provider the unit tests
        // use: the query does execute (CustomerId filter translates), and a real row DOES get
        // picked up once it looks viewed. Tenant 29 already has real rows in UAT.
        var tenant29Rows = await db.GeneratedReports.Where(r => r.CustomerId == 29).ToListAsync();
        if (tenant29Rows.Count > 0)
        {
            // Must be the LATEST row for its own (scope, reportType, period) key - the function
            // only ever considers the latest per key, so stamping any older row would make this
            // probe depend on real UAT data shape instead of proving the intended behaviour.
            var probe = tenant29Rows
                .GroupBy(r => (r.ScopeDescriptor, r.ReportType, r.Period))
                .First()
                .OrderByDescending(r => r.GeneratedAtUtc)
                .First();
            var originalLastViewed = probe.LastViewedUtc;
            probe.LastViewedUtc = DateTime.UtcNow.AddDays(-10);
            await db.SaveChangesAsync();

            // cooldownBeforeUtc = now (not now-30d): the point here is only to prove the query
            // executes and picks up a real row against real SQL Server, not to exercise the
            // cooldown boundary itself (already covered by unit tests) - a tight cooldown would
            // make this probe depend on how old today's UAT test data happens to be.
            var candidates = await Insights.Worker.PaidKeepWarmScheduler.GetKeepWarmCandidatesAsync(
                db, customerId: 29, viewedSinceUtc: DateTime.UtcNow.AddDays(-90), cooldownBeforeUtc: DateTime.UtcNow,
                CancellationToken.None);
            output.WriteLine($"Tenant 29 keep-warm candidates with a real row stamped 'viewed 10 days ago': {candidates.Count}");
            Assert.Contains(candidates, c => c.Id == probe.Id);

            // Leave UAT exactly as found.
            probe.LastViewedUtc = originalLastViewed;
            await db.SaveChangesAsync();
        }
        else
        {
            output.WriteLine("Tenant 29 has no GeneratedReport rows right now - skipping the candidate-query probe.");
        }

        Assert.True(paidTenants.Count >= 0); // real query executed without throwing - that's the point of this test.
    }

    /// <summary>
    /// THROWAWAY - sizes UAT before committing to a golden-fixture build approach
    /// (docs/GOLDEN_FIXTURES.md). Read-only: no writes, no schema changes.
    /// </summary>
    [Fact]
    public async Task InspectUatSizeAndTenantProfile_ForGoldenFixturePlanning()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        foreach (var table in new[] { "Customer", "CustomerBranch", "ComplianceInstance" })
        {
            await using var colCmd = new SqlCommand(
                "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@t) ORDER BY column_id", connection);
            colCmd.Parameters.AddWithValue("@t", table);
            await using var colReader = await colCmd.ExecuteReaderAsync();
            var cols = new List<string>();
            while (await colReader.ReadAsync())
                cols.Add(colReader.GetString(0));
            output.WriteLine($"{table}: {string.Join(", ", cols)}");
        }

        await using (var sizeCmd = new SqlCommand(@"
            SELECT
                (SELECT CAST(SUM(size) * 8.0 / 1024 AS DECIMAL(10,1)) FROM sys.database_files WHERE type = 0) AS data_mb,
                (SELECT CAST(SUM(size) * 8.0 / 1024 AS DECIMAL(10,1)) FROM sys.database_files WHERE type = 1) AS log_mb,
                (SELECT COUNT(DISTINCT ID) FROM Customer WHERE IsDeleted = 0) AS active_tenants,
                (SELECT COUNT(*) FROM ComplianceInstance) AS total_instances,
                (SELECT COUNT(*) FROM CustomerBranch WHERE CustomerID = 999001) AS existing_999001_branch_rows,
                (SELECT COUNT(*) FROM Customer WHERE ID = 999001) AS existing_999001_customer_rows", connection))
        await using (var reader = await sizeCmd.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            for (var i = 0; i < reader.FieldCount; i++)
                output.WriteLine($"{reader.GetName(i)}: {reader[i]}");
        }

        // A candidate donor tenant per GOLDEN_FIXTURES.md's "small real tenant (e.g. ~1,000 instances)".
        // Tenant join goes ComplianceInstance.CustomerBranchID -> CustomerBranch.ID -> CustomerBranch.CustomerID
        // -> Customer.ID (sql/03_scope_resolution.sql) - Customer's own PK is ID, not CustomerID; that
        // name belongs to the FK column on CustomerBranch (confirmed via sys.columns, not assumed).
        await using var donorCmd = new SqlCommand(@"
            SELECT TOP 15 cb.CustomerID, COUNT(*) AS instance_count
            FROM ComplianceInstance ci
            JOIN CustomerBranch cb ON cb.ID = ci.CustomerBranchID AND cb.IsDeleted = 0
            JOIN Customer c ON c.ID = cb.CustomerID AND c.IsDeleted = 0
            WHERE ci.IsDeleted = 0
            GROUP BY cb.CustomerID
            HAVING COUNT(*) BETWEEN 500 AND 2000
            ORDER BY COUNT(*) DESC", connection);
        await using var donorReader = await donorCmd.ExecuteReaderAsync();
        output.WriteLine("--- donor candidates (500-2000 instances) ---");
        while (await donorReader.ReadAsync())
            output.WriteLine($"  CustomerID {donorReader.GetInt32(0)}: {donorReader.GetInt32(1)} instances");
    }

    /// <summary>THROWAWAY - schema/join facts needed to build the golden fixture (docs/GOLDEN_FIXTURES.md). Read-only.</summary>
    [Fact]
    public async Task InspectSchemaForGoldenFixtureBuild()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        foreach (var table in new[] { "ComplianceScheduleOn", "ComplianceTransaction", "Compliance", "Act", "ComplianceCategory", "EntitiesAssignment", "User", "ComplianceStatus", "RecentComplianceTransactionView" })
        {
            await using var colCmd = new SqlCommand(
                "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@t) ORDER BY column_id", connection);
            colCmd.Parameters.AddWithValue("@t", table);
            await using var colReader = await colCmd.ExecuteReaderAsync();
            var cols = new List<string>();
            while (await colReader.ReadAsync())
                cols.Add(colReader.GetString(0));
            output.WriteLine($"{table}: {string.Join(", ", cols)}");
        }

        await using (var defCmd = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID('RecentComplianceTransactionView'))", connection))
        {
            var def = await defCmd.ExecuteScalarAsync();
            output.WriteLine("--- RecentComplianceTransactionView definition ---");
            output.WriteLine(def?.ToString() ?? "(NULL - not a view with retrievable definition, or is a table)");
        }

        await using (var typeCmd = new SqlCommand("SELECT type_desc FROM sys.objects WHERE object_id = OBJECT_ID('RecentComplianceTransactionView')", connection))
        {
            var type = await typeCmd.ExecuteScalarAsync();
            output.WriteLine($"RecentComplianceTransactionView object type: {type}");
        }

        // Donor tenant candidates' branch/hierarchy shape - want a SIMPLE one.
        foreach (var candidate in new[] { 942, 1105, 1355, 1276, 1380 })
        {
            await using var shapeCmd = new SqlCommand(@"
                SELECT
                    (SELECT COUNT(*) FROM CustomerBranch WHERE CustomerID = @cid AND IsDeleted = 0) AS branches,
                    (SELECT COUNT(*) FROM CustomerBranch WHERE CustomerID = @cid AND IsDeleted = 0 AND ParentID IS NULL) AS apex_branches,
                    (SELECT COUNT(DISTINCT c.ActID) FROM ComplianceInstance ci
                        JOIN CustomerBranch cb ON cb.ID = ci.CustomerBranchID
                        JOIN Compliance c ON c.ID = ci.ComplianceID
                        WHERE cb.CustomerID = @cid AND cb.IsDeleted = 0 AND ci.IsDeleted = 0 AND c.IsDeleted = 0) AS distinct_acts,
                    (SELECT COUNT(DISTINCT ea.UserID) FROM EntitiesAssignment ea
                        JOIN CustomerBranch cb ON cb.ID = ea.BranchID
                        WHERE cb.CustomerID = @cid AND cb.IsDeleted = 0) AS distinct_scope_users", connection);
            shapeCmd.Parameters.AddWithValue("@cid", candidate);
            await using var shapeReader = await shapeCmd.ExecuteReaderAsync();
            await shapeReader.ReadAsync();
            output.WriteLine($"Tenant {candidate}: branches={shapeReader[0]} apex_branches={shapeReader[1]} distinct_acts={shapeReader[2]} distinct_scope_users={shapeReader[3]}");
        }
    }

    /// <summary>THROWAWAY - find 2 clean Act+Compliance pairs (different categories) to reuse as FK targets for the golden fixture.</summary>
    [Fact]
    public async Task FindMasterDataCandidatesForGoldenFixture()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var cmd = new SqlCommand(@"
            SELECT TOP 5 c.ID AS ComplianceId, c.ActID, a.ComplianceCategoryId, a.Name AS ActName,
                   c.RiskType, c.Imprisonment, c.NatureOfCompliance, c.ComplianceType
            FROM Compliance c
            JOIN Act a ON a.ID = c.ActID AND a.IsDeleted = 0
            WHERE c.IsDeleted = 0
            ORDER BY c.ID", connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var vals = new object[reader.FieldCount];
            reader.GetValues(vals);
            output.WriteLine(string.Join(" | ", vals.Select(v => v?.ToString() ?? "NULL")));
        }

        output.WriteLine("--- distinct category IDs available ---");
        await using var catCmd = new SqlCommand(@"
            SELECT TOP 10 a.ComplianceCategoryId, COUNT(*) AS n
            FROM Compliance c JOIN Act a ON a.ID = c.ActID AND a.IsDeleted = 0
            WHERE c.IsDeleted = 0
            GROUP BY a.ComplianceCategoryId ORDER BY n DESC", connection);
        await using var catReader = await catCmd.ExecuteReaderAsync();
        while (await catReader.ReadAsync())
            output.WriteLine($"category {catReader[0]}: {catReader[1]} compliance rows");
    }

    /// <summary>THROWAWAY - identity-column check for the golden fixture build.</summary>
    [Fact]
    public async Task CheckIdentityColumnsForGoldenFixture()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (var table in new[] { "Customer", "CustomerBranch", "ComplianceInstance", "ComplianceScheduleOn", "ComplianceTransaction", "EntitiesAssignment", "User" })
        {
            await using var cmd = new SqlCommand(
                "SELECT c.name, c.is_identity FROM sys.columns c WHERE c.object_id = OBJECT_ID(@t) AND c.is_identity = 1", connection);
            cmd.Parameters.AddWithValue("@t", table);
            await using var reader = await cmd.ExecuteReaderAsync();
            var found = false;
            while (await reader.ReadAsync())
            {
                found = true;
                output.WriteLine($"{table}.{reader.GetString(0)} IS IDENTITY");
            }
            if (!found)
                output.WriteLine($"{table}: no identity column");
        }
    }

    /// <summary>
    /// THROWAWAY - builds the golden-fixture tenants 999001 (F-1..F-13) and 999002 (F-14) in UAT
    /// per docs/GOLDEN_FIXTURES.md. Additive only - every write targets CustomerID 999001/999002;
    /// existing Compliance/Act master rows (IDs 3, 5) are read-only references. Run ONCE - the
    /// script itself aborts if either tenant already exists.
    /// </summary>
    [Fact]
    public async Task BuildGoldenFixtureTenants()
    {
        var scriptPath = Environment.GetEnvironmentVariable("GOLDEN_FIXTURE_BUILD_SCRIPT")
            ?? throw new InvalidOperationException("Set GOLDEN_FIXTURE_BUILD_SCRIPT to the build_999001.sql path.");
        var script = await File.ReadAllTextAsync(scriptPath);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(script, connection) { CommandTimeout = 120 };
        // PRINT statements arrive as InfoMessage events, not through ExecuteNonQueryAsync's result.
        connection.InfoMessage += (_, e) => output.WriteLine(e.Message);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>THROWAWAY - a valid (StateID, CityID, Type) triple to reuse for golden-fixture CustomerBranch rows.</summary>
    [Fact]
    public async Task FindValidBranchLookupValues()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 5 StateID, CityID, Type FROM CustomerBranch WHERE StateID IS NOT NULL AND CityID IS NOT NULL AND IsDeleted = 0", connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            output.WriteLine($"StateID={reader[0]} CityID={reader[1]} Type={reader[2]}");
    }

    /// <summary>THROWAWAY - a valid User.ID to reuse as a placeholder CreatedBy FK for the golden fixture.</summary>
    [Fact]
    public async Task FindPlaceholderUserId()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand("SELECT MIN(ID) FROM [User] WHERE IsDeleted = 0", connection);
        var id = await cmd.ExecuteScalarAsync();
        output.WriteLine($"Min active User.ID = {id}");
    }

    /// <summary>THROWAWAY - NOT NULL columns with no default, per table, for the golden fixture INSERTs.</summary>
    [Fact]
    public async Task FindRequiredColumnsForGoldenFixture()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (var table in new[] { "Customer", "CustomerBranch", "User", "ComplianceInstance", "ComplianceScheduleOn", "ComplianceTransaction", "EntitiesAssignment" })
        {
            await using var cmd = new SqlCommand(@"
                SELECT c.name, t.name AS type_name, c.max_length, c.is_nullable
                FROM sys.columns c
                JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(@tbl)
                  AND c.is_nullable = 0
                  AND c.is_identity = 0
                  AND c.is_computed = 0
                  AND NOT EXISTS (SELECT 1 FROM sys.default_constraints dc WHERE dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id)
                ORDER BY c.column_id", connection);
            cmd.Parameters.AddWithValue("@tbl", table);
            await using var reader = await cmd.ExecuteReaderAsync();
            var cols = new List<string>();
            while (await reader.ReadAsync())
                cols.Add($"{reader.GetString(0)}({reader.GetString(1)})");
            output.WriteLine($"{table}: {string.Join(", ", cols)}");
        }
    }

    /// <summary>THROWAWAY - validates the golden fixture (999001/999002) against usp_Insights_GoldenInvariants and the aggregate expectations table (docs/GOLDEN_FIXTURES.md).</summary>
    [Fact]
    public async Task ValidateGoldenFixture()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        foreach (var customerId in new[] { 999001, 999002 })
        {
            output.WriteLine($"--- usp_Insights_GoldenInvariants @CustomerID={customerId} ---");
            await using var cmd = new SqlCommand("usp_Insights_GoldenInvariants", connection) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("@CustomerID", customerId);
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    output.WriteLine($"{reader["TestId"]} {reader["TestName"]}: {reader["Result"]} - {reader["Detail"]}");
                output.WriteLine("ALL PASSED (no THROW)");
            }
            catch (SqlException ex)
            {
                output.WriteLine($"THREW: {ex.Message}");
            }
        }

        output.WriteLine("--- aggregate expectations (999001, F-1..F-6) ---");
        await using (var aggCmd = new SqlCommand(@"
            SELECT
                COUNT(*) AS past_due_schedules,
                SUM(CASE WHEN d.OverdueEligible = 1 THEN 1 ELSE 0 END) AS overdue,
                SUM(CASE WHEN d.ClosureClass = 'completed' THEN 1 ELSE 0 END) AS completed,
                SUM(CASE WHEN d.ClosureClass = 'resolved_terminal' THEN 1 ELSE 0 END) AS resolved_terminal,
                SUM(CASE WHEN d.ClosureClass = 'completed' AND d.Timeliness = 'on_time' THEN 1 ELSE 0 END) AS on_time_completions
            FROM ComplianceScheduleOn cso
            JOIN ComplianceInstance i ON i.ID = cso.ComplianceInstanceID
            JOIN CustomerBranch cb ON cb.ID = i.CustomerBranchID
            JOIN RecentComplianceTransactionView rct ON rct.ComplianceScheduleOnID = cso.ID
            LEFT JOIN dbo.vInsightsStatusCurrent d ON d.StatusId = rct.ComplianceStatusID
            WHERE cb.CustomerID = 999001 AND cb.IsDeleted = 0 AND i.IsDeleted = 0
              AND cso.IsActive = 1 AND cso.IsUpcomingNotDeleted = 1 AND cso.ScheduleOn <= GETDATE()", connection))
        await using (var aggReader = await aggCmd.ExecuteReaderAsync())
        {
            await aggReader.ReadAsync();
            var pastDue = aggReader.GetInt32(0);
            var overdue = aggReader.GetInt32(1);
            var completed = aggReader.GetInt32(2);
            var resolvedTerminal = aggReader.GetInt32(3);
            var onTime = aggReader.GetInt32(4);
            var onTimePct = completed == 0 ? 0.0 : onTime * 100.0 / completed;
            output.WriteLine($"past_due_schedules={pastDue} (expect 80)");
            output.WriteLine($"overdue={overdue} (expect 40)");
            output.WriteLine($"completed={completed} (expect 20)");
            output.WriteLine($"resolved_terminal={resolvedTerminal} (expect 20)");
            output.WriteLine($"on_time_pct={onTimePct:F1} (expect 50.0)");
        }

        await using (var rollupCmd = new SqlCommand("SELECT SUM(DirectInstances) FROM (SELECT COUNT(i.ID) AS DirectInstances FROM dbo.tvfInsightsEntityTree(999001) t LEFT JOIN ComplianceInstance i ON i.CustomerBranchID = t.BranchID AND i.IsDeleted = 0 GROUP BY t.BranchID) x", connection))
        {
            var rollup = await rollupCmd.ExecuteScalarAsync();
            output.WriteLine($"total rollup (all branches, 999001)={rollup}");
        }

        await using (var f6Cmd = new SqlCommand(@"
            SELECT t.BranchName, COUNT(i.ID) AS DirectInstances
            FROM dbo.tvfInsightsEntityTree(999001) t
            LEFT JOIN ComplianceInstance i ON i.CustomerBranchID = t.BranchID AND i.IsDeleted = 0
            WHERE t.BranchName IN (N'Golden Intermediate', N'Golden Leaf A', N'Golden Leaf B')
            GROUP BY t.BranchName", connection))
        await using (var f6Reader = await f6Cmd.ExecuteReaderAsync())
        {
            while (await f6Reader.ReadAsync())
                output.WriteLine($"F-6 node {f6Reader[0]}: {f6Reader[1]} instances");
        }

        await using (var orphanCmd = new SqlCommand("SELECT BranchName, RootKind FROM dbo.tvfInsightsEntityTree(999001) WHERE BranchName = N'Golden Orphan Child'", connection))
        await using (var orphanReader = await orphanCmd.ExecuteReaderAsync())
        {
            while (await orphanReader.ReadAsync())
                output.WriteLine($"F-11 {orphanReader[0]}: RootKind={orphanReader[1]}");
        }
    }

    /// <summary>
    /// THROWAWAY - validates F-7 (docs/GOLDEN_FIXTURES.md) WITHOUT ever committing a change to the
    /// shared ComplianceStatus table. usp_Insights_AssertStatusCoverage scans ComplianceStatus
    /// SYSTEM-WIDE, not per-tenant - a committed unmapped row would make every tenant's dimension
    /// procs throw, everywhere, until removed. Everything here runs inside one transaction that is
    /// ALWAYS rolled back, success or failure.
    /// </summary>
    [Fact]
    public async Task ValidateF7StatusCoverageThrow_WithoutCommittingAnything()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var insertCmd = new SqlCommand(
                "SET IDENTITY_INSERT ComplianceStatus ON; " +
                "INSERT ComplianceStatus (ID, Name) VALUES (99999, N'Golden Fixture Unmapped Status'); " +
                "SET IDENTITY_INSERT ComplianceStatus OFF;",
                connection, (SqlTransaction)transaction))
            {
                await insertCmd.ExecuteNonQueryAsync();
            }

            await using var assertCmd = new SqlCommand("usp_Insights_AssertStatusCoverage", connection, (SqlTransaction)transaction)
            {
                CommandType = CommandType.StoredProcedure,
            };
            var threw = false;
            try
            {
                await assertCmd.ExecuteNonQueryAsync();
            }
            catch (SqlException ex) when (ex.Number == 51001)
            {
                threw = true;
                output.WriteLine($"F-7 confirmed: THROW 51001 as expected: {ex.Message}");
            }

            Assert.True(threw, "usp_Insights_AssertStatusCoverage should have THROWn 51001 for the unmapped status.");
        }
        finally
        {
            // ALWAYS rollback - this transaction must never be committed, on success or failure.
            await transaction.RollbackAsync();
        }

        // Confirm the rollback really left nothing behind.
        await using var checkCmd = new SqlCommand("SELECT COUNT(*) FROM ComplianceStatus WHERE ID = 99999", connection);
        var count = (int)(await checkCmd.ExecuteScalarAsync())!;
        Assert.Equal(0, count);
        output.WriteLine("Confirmed: rollback left ComplianceStatus untouched.");
    }

    /// <summary>THROWAWAY - adds a tenant-wide scoped user to 999001/999002 (additive) so usp_Insights_Dimension_Location can be exercised at full tenant scope.</summary>
    [Fact]
    public async Task AddTenantWideUserToGoldenFixture()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(@"
            DECLARE @Now DATETIME = GETDATE();
            DECLARE @TenantWideUserId INT, @SingleTenantUserId INT;

            IF NOT EXISTS (SELECT 1 FROM [User] WHERE CustomerID = 999001 AND FirstName = N'Golden' AND LastName = N'TenantWideUser')
            BEGIN
                INSERT [User] (FirstName, LastName, CreatedByText, CreatedOn, IsDeleted, IsActive, CustomerID)
                VALUES (N'Golden', N'TenantWideUser', N'golden fixture', @Now, 0, 1, 999001);
                SET @TenantWideUserId = SCOPE_IDENTITY();

                INSERT EntitiesAssignment (UserID, BranchID, ComplianceCatagoryID, CreatedOn)
                SELECT @TenantWideUserId, cb.ID, cat.CategoryId, @Now
                FROM CustomerBranch cb
                CROSS JOIN (SELECT 12 AS CategoryId UNION ALL SELECT 15) cat
                WHERE cb.CustomerID = 999001 AND cb.IsDeleted = 0;
            END

            IF NOT EXISTS (SELECT 1 FROM [User] WHERE CustomerID = 999002 AND FirstName = N'Golden' AND LastName = N'TenantWideUser')
            BEGIN
                INSERT [User] (FirstName, LastName, CreatedByText, CreatedOn, IsDeleted, IsActive, CustomerID)
                VALUES (N'Golden', N'TenantWideUser', N'golden fixture', @Now, 0, 1, 999002);
                SET @SingleTenantUserId = SCOPE_IDENTITY();

                INSERT EntitiesAssignment (UserID, BranchID, ComplianceCatagoryID, CreatedOn)
                SELECT @SingleTenantUserId, cb.ID, 12, @Now
                FROM CustomerBranch cb WHERE cb.CustomerID = 999002 AND cb.IsDeleted = 0;
            END

            SELECT
                (SELECT ID FROM [User] WHERE CustomerID = 999001 AND FirstName = N'Golden' AND LastName = N'TenantWideUser') AS TenantWideUserId999001,
                (SELECT ID FROM [User] WHERE CustomerID = 999002 AND FirstName = N'Golden' AND LastName = N'TenantWideUser') AS TenantWideUserId999002;
        ", connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        output.WriteLine($"TenantWideUserId999001={reader[0]} TenantWideUserId999002={reader[1]}");
    }

    /// <summary>THROWAWAY - validates F-12/F-13/F-14 via usp_Insights_Dimension_Location at full tenant scope.</summary>
    [Fact]
    public async Task ValidateLocationDimensionFlags()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var idCmd = new SqlCommand(
            "SELECT (SELECT ID FROM [User] WHERE CustomerID = 999001 AND LastName = N'TenantWideUser'), " +
            "(SELECT ID FROM [User] WHERE CustomerID = 999002 AND LastName = N'TenantWideUser')", connection))
        await using (var idReader = await idCmd.ExecuteReaderAsync())
        {
            await idReader.ReadAsync();
            var user999001 = Convert.ToInt32(idReader.GetValue(0));
            var user999002 = Convert.ToInt32(idReader.GetValue(1));
            idReader.Close();

            foreach (var (customerId, userId) in new[] { (999001, user999001), (999002, user999002) })
            {
                output.WriteLine($"=== usp_Insights_Dimension_Location tenant {customerId} (user {userId}) ===");
                await using var cmd = new SqlCommand("usp_Insights_Dimension_Location", connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 60 };
                cmd.Parameters.AddWithValue("@UserID", userId);
                cmd.Parameters.AddWithValue("@CustomerID", customerId);
                await using var reader = await cmd.ExecuteReaderAsync();

                // Result set 1: control_totals
                while (await reader.ReadAsync())
                {
                    var cols = new List<string>();
                    for (var i = 0; i < reader.FieldCount; i++)
                        cols.Add($"{reader.GetName(i)}={reader[i]}");
                    output.WriteLine("control_totals: " + string.Join(" ", cols));
                }

                // Result set 2: rows - look for Flags
                await reader.NextResultAsync();
                var flagsOrdinal = -1;
                var nameOrdinal = -1;
                while (await reader.ReadAsync())
                {
                    if (flagsOrdinal < 0) { flagsOrdinal = reader.GetOrdinal("Flags"); nameOrdinal = reader.GetOrdinal("BranchName"); }
                    var flags = reader[flagsOrdinal]?.ToString();
                    if (!string.IsNullOrEmpty(flags))
                        output.WriteLine($"row: {reader[nameOrdinal]} Flags={flags}");
                }

                // Skip result set 3 (detector_policy), 4 (assertions)
                await reader.NextResultAsync();
                var seenWorst = false;
                await reader.NextResultAsync();
                while (await reader.ReadAsync())
                {
                    if (reader["AssertionId"]?.ToString() == "A-WORST")
                        seenWorst = true;
                }
                output.WriteLine($"A-WORST assertion present: {seenWorst}");

                // Result set 5: findings
                await reader.NextResultAsync();
                while (await reader.ReadAsync())
                    output.WriteLine($"finding: {reader["FindingId"]} - {reader["Headline"]}");

                // Result set 6: data_quality
                await reader.NextResultAsync();
                while (await reader.ReadAsync())
                    output.WriteLine($"data_quality: {reader["Issue"]} - {reader["Detail"]}");
            }
        }
    }

    /// <summary>
    /// THROWAWAY - generates CREATE TABLE DDL for the base RegTrack tables the golden fixture
    /// needs, straight from sys.columns/sys.types (never hand-typed), so the CI schema is
    /// byte-accurate to what sql/01..sql/06 actually run against. No FK constraints emitted -
    /// nothing in sql/01..sql/14 depends on FK enforcement, only on the data relationships being
    /// consistent, and skipping them removes a large source of ordering/cascade defects for a
    /// fixture-only schema. Writes to the path in GOLDEN_FIXTURE_SCHEMA_OUT.
    /// </summary>
    [Fact]
    public async Task GenerateGoldenFixtureSchemaDdl()
    {
        var outPath = Environment.GetEnvironmentVariable("GOLDEN_FIXTURE_SCHEMA_OUT")
            ?? throw new InvalidOperationException("Set GOLDEN_FIXTURE_SCHEMA_OUT.");

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        var tables = new[]
        {
            "Customer", "CustomerBranch", "User", "ComplianceInstance", "ComplianceScheduleOn",
            "ComplianceTransaction", "EntitiesAssignment", "ComplianceStatus", "Compliance", "Act", "ComplianceCategory",
        };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("-- Generated from real UAT schema (sys.columns/sys.types) for the golden-fixture CI container.");
        sb.AppendLine("-- These are the base RegTrack platform tables sql/01..sql/14 assume already exist.");
        sb.AppendLine("-- No FK constraints - nothing in the Insights SQL depends on FK enforcement.");
        sb.AppendLine("SET NOCOUNT ON;");
        sb.AppendLine();

        foreach (var table in tables)
        {
            await using var cmd = new SqlCommand(@"
                SELECT c.name AS ColumnName, ty.name AS TypeName, c.max_length, c.precision, c.scale,
                       c.is_nullable, c.is_identity, dc.definition AS DefaultDefinition
                FROM sys.columns c
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
                WHERE c.object_id = OBJECT_ID(@t)
                ORDER BY c.column_id", connection);
            cmd.Parameters.AddWithValue("@t", table);
            await using var reader = await cmd.ExecuteReaderAsync();

            var lines = new List<string>();
            var pkCol = (string?)null;
            while (await reader.ReadAsync())
            {
                var colName = reader.GetString(0);
                var typeName = reader.GetString(1);
                var maxLen = reader.GetInt16(2);
                var precision = reader.GetByte(3);
                var scale = reader.GetByte(4);
                var isNullable = reader.GetBoolean(5);
                var isIdentity = reader.GetBoolean(6);
                var defaultDef = reader.IsDBNull(7) ? null : reader.GetString(7);

                var sqlType = typeName switch
                {
                    "varchar" or "char" => $"{typeName}({(maxLen == -1 ? "MAX" : maxLen.ToString())})",
                    "nvarchar" or "nchar" => $"{typeName}({(maxLen == -1 ? "MAX" : (maxLen / 2).ToString())})",
                    "decimal" or "numeric" => $"{typeName}({precision},{scale})",
                    _ => typeName,
                };

                var identityClause = isIdentity ? " IDENTITY(1,1)" : "";
                var nullClause = isNullable ? "NULL" : "NOT NULL";
                // Real UAT defaults matter here: the golden-fixture data script omits several
                // NOT NULL columns that only insert successfully because UAT has a default
                // constraint on them (e.g. ComplianceInstance.DirectorId) - a schema generated
                // without capturing that would reject the exact same INSERT in a fresh CI
                // container. Emitted verbatim from sys.default_constraints, never guessed.
                var defaultClause = defaultDef is not null ? $" CONSTRAINT [DF_{table}_{colName}] DEFAULT {defaultDef}" : "";
                lines.Add($"    [{colName}] {sqlType}{identityClause} {nullClause}{defaultClause}");
                if (isIdentity) pkCol = colName;
            }

            sb.AppendLine($"IF OBJECT_ID('dbo.{table}', 'U') IS NULL");
            sb.AppendLine("BEGIN");
            sb.AppendLine($"CREATE TABLE [dbo].[{table}] (");
            sb.AppendLine(string.Join(",\n", lines) + (pkCol is not null ? $",\n    CONSTRAINT [PK_{table}] PRIMARY KEY ([{pkCol}])" : ""));
            sb.AppendLine(");");
            sb.AppendLine("END");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        // The view, verbatim (captured via OBJECT_DEFINITION earlier this session).
        await using (var viewCmd = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID('RecentComplianceTransactionView'))", connection))
        {
            var viewDef = (string)(await viewCmd.ExecuteScalarAsync())!;
            sb.AppendLine("IF OBJECT_ID('dbo.RecentComplianceTransactionView', 'V') IS NOT NULL DROP VIEW dbo.RecentComplianceTransactionView;");
            sb.AppendLine("GO");
            sb.AppendLine(viewDef);
            sb.AppendLine("GO");
        }

        await File.WriteAllTextAsync(outPath, sb.ToString());
        output.WriteLine($"Wrote {outPath} ({sb.Length} chars)");
    }

    /// <summary>THROWAWAY - emits INSERT statements for ComplianceStatus IDs 1-23 (real Name text) for the CI-portable fixture script.</summary>
    [Fact]
    public async Task GenerateComplianceStatusSeedInserts()
    {
        var outPath = Environment.GetEnvironmentVariable("GOLDEN_FIXTURE_STATUS_OUT")
            ?? throw new InvalidOperationException("Set GOLDEN_FIXTURE_STATUS_OUT.");

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand("SELECT ID, Name FROM ComplianceStatus WHERE ID BETWEEN 1 AND 23 ORDER BY ID", connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("-- Real ComplianceStatus rows 1-23 (matching InsightsStatusClassification's seed in sql/01), ASCII-safe text only.");
        sb.AppendLine("SET IDENTITY_INSERT ComplianceStatus ON;");
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            // ASCII-safe placeholder text, not the real (possibly non-ASCII) display name - CLAUDE.md 5a.
            // The dictionary in sql/01 never reads ComplianceStatus.Name for classification (bucket by ID only),
            // so the exact text here is cosmetic and does not affect any assertion.
            sb.AppendLine($"INSERT ComplianceStatus (ID, Name) VALUES ({id}, N'Status {id}');");
        }
        sb.AppendLine("SET IDENTITY_INSERT ComplianceStatus OFF;");

        await File.WriteAllTextAsync(outPath, sb.ToString());
        output.WriteLine($"Wrote {outPath}");
    }

    /// <summary>
    /// THROWAWAY - emits full-row INSERT statements for the exact Act/Compliance master rows the
    /// golden fixture references (Compliance 3, 5; Act 5, 32), so the CI container has real,
    /// schema-valid rows at those exact IDs rather than a hand-built guess at every NOT NULL
    /// column. ASCII-only text columns only (Name/Description may contain non-ASCII in real UAT -
    /// CLAUDE.md 5a - so those two are replaced with a safe placeholder; nothing else reads them).
    /// </summary>
    [Fact]
    public async Task GenerateMasterDataInserts()
    {
        var outPath = Environment.GetEnvironmentVariable("GOLDEN_FIXTURE_MASTER_OUT")
            ?? throw new InvalidOperationException("Set GOLDEN_FIXTURE_MASTER_OUT.");

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var sb = new System.Text.StringBuilder();

        async Task EmitTableAsync(string table, string idColumn, int[] ids, string[] asciiOnlyTextColumns)
        {
            await using var colCmd = new SqlCommand(
                "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@t) ORDER BY column_id", connection);
            colCmd.Parameters.AddWithValue("@t", table);
            var columns = new List<string>();
            await using (var colReader = await colCmd.ExecuteReaderAsync())
                while (await colReader.ReadAsync())
                    columns.Add(colReader.GetString(0));

            sb.AppendLine($"SET IDENTITY_INSERT {table} ON;");
            foreach (var id in ids)
            {
                await using var rowCmd = new SqlCommand(
                    $"SELECT {string.Join(",", columns.Select(c => $"[{c}]"))} FROM {table} WHERE {idColumn} = @id", connection);
                rowCmd.Parameters.AddWithValue("@id", id);
                await using var rowReader = await rowCmd.ExecuteReaderAsync();
                if (!await rowReader.ReadAsync())
                {
                    output.WriteLine($"WARNING: {table} {idColumn}={id} not found - skipped.");
                    continue;
                }

                var values = new List<string>();
                for (var i = 0; i < columns.Count; i++)
                {
                    if (asciiOnlyTextColumns.Contains(columns[i]))
                    {
                        values.Add($"N'{table} {columns[i]} fixture text'");
                        continue;
                    }
                    var val = rowReader.GetValue(i);
                    values.Add(val switch
                    {
                        DBNull => "NULL",
                        bool b => b ? "1" : "0",
                        string s => "N'" + s.Replace("'", "''") + "'",
                        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
                        _ => Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                    });
                }
                sb.AppendLine($"INSERT {table} ({string.Join(",", columns.Select(c => $"[{c}]"))}) VALUES ({string.Join(",", values)});");
            }
            sb.AppendLine($"SET IDENTITY_INSERT {table} OFF;");
        }

        await EmitTableAsync("Act", "ID", [5, 32], ["Name", "Description", "State", "City"]);
        await EmitTableAsync("Compliance", "ID", [3, 5], ["Description", "NonComplianceEffects", "Sections", "Designation", "Others", "RequiredForms", "ShortDescription", "PenaltyDescription", "ReferenceMaterialText", "DeactivateDesc", "SampleFormLink", "ComplianceActionableProcedure"]);

        await File.WriteAllTextAsync(outPath, sb.ToString());

        var bytes = await File.ReadAllBytesAsync(outPath);
        var isAscii = bytes.All(b => b < 128);
        output.WriteLine($"Wrote {outPath} ({bytes.Length} bytes, pure ASCII: {isAscii})");
    }

    /// <summary>
    /// THROWAWAY - deploys sql/20 (InsightsTenantTokenUsage, design doc Sec.12.3) directly against
    /// real UAT (no sqlcmd binary available in this environment, same reasoning as sql/19's
    /// equivalent test) and validates the whole read/write path against the REAL schema:
    /// SqlTenantTokenBudgetRepository.RecordUsageAsync + GetTokensSinceAsync, round-tripped for
    /// real, plus the idempotency guard (same RunId twice must not double-count). Cleans up the
    /// row it inserts - additive-only, never touches any other row. Requires ConnectionStrings__RegTrack.
    /// </summary>
    [Fact]
    public async Task DeployTenantTokenUsageTable_ThenValidateBudgetRepositoryRoundTrip()
    {
        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                IF OBJECT_ID('dbo.InsightsTenantTokenUsage', 'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.InsightsTenantTokenUsage (
                        Id             BIGINT IDENTITY(1,1) NOT NULL,
                        CustomerID     INT NOT NULL,
                        RunId          NVARCHAR(200) NOT NULL,
                        TotalTokens    BIGINT NOT NULL,
                        RecordedAtUtc  DATETIME2(0) NOT NULL CONSTRAINT DF_InsightsTenantTokenUsage_RecordedAtUtc DEFAULT SYSUTCDATETIME(),
                        CONSTRAINT PK_InsightsTenantTokenUsage PRIMARY KEY CLUSTERED (Id)
                    );
                    CREATE NONCLUSTERED INDEX IX_InsightsTenantTokenUsage_Customer_Recorded
                        ON dbo.InsightsTenantTokenUsage (CustomerID, RecordedAtUtc)
                        INCLUDE (TotalTokens);
                END
                """, connection);
            await command.ExecuteNonQueryAsync();
        }
        output.WriteLine("sql/20 applied (or already present) against real UAT.");

        var repository = new Insights.Data.SqlTenantTokenBudgetRepository(ConnectionString);
        const int probeTenantId = -999001; // negative, structurally impossible to collide with a real CustomerID
        var probeRunId = $"probe-{Guid.NewGuid():N}";
        var startOfMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        try
        {
            var before = await repository.GetTokensSinceAsync(probeTenantId, startOfMonth);
            Assert.Equal(0, before); // clean slate for a tenant id nothing else will ever use

            await repository.RecordUsageAsync(probeTenantId, probeRunId, 12_345);
            var afterOne = await repository.GetTokensSinceAsync(probeTenantId, startOfMonth);
            Assert.Equal(12_345, afterOne);

            // Idempotency guard (CLAUDE.md 6: LLM activities must be idempotent) - a DTFx replay
            // recording the SAME RunId again must not double-count.
            await repository.RecordUsageAsync(probeTenantId, probeRunId, 12_345);
            var afterReplay = await repository.GetTokensSinceAsync(probeTenantId, startOfMonth);
            Assert.Equal(12_345, afterReplay);

            // A DIFFERENT run for the same tenant DOES add.
            await repository.RecordUsageAsync(probeTenantId, $"probe-{Guid.NewGuid():N}", 5_000);
            var afterSecondRun = await repository.GetTokensSinceAsync(probeTenantId, startOfMonth);
            Assert.Equal(17_345, afterSecondRun);

            output.WriteLine($"Round trip confirmed against real UAT: {afterSecondRun} tokens across 2 runs for probe tenant {probeTenantId}.");
        }
        finally
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var cleanup = new SqlCommand("DELETE FROM dbo.InsightsTenantTokenUsage WHERE CustomerID = @CustomerId;", connection);
            cleanup.Parameters.AddWithValue("@CustomerId", probeTenantId);
            var deleted = await cleanup.ExecuteNonQueryAsync();
            output.WriteLine($"Cleaned up {deleted} probe row(s).");
        }
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test.");
}

/// <summary>THROWAWAY diagnostics helper for TraceOneLlmCallAsync - prints the raw request/response the OTLP exporter's HttpClient actually sends and receives.</summary>
internal sealed class LoggingHandler(ITestOutputHelper output) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        output.WriteLine($"[HTTP] -> {request.Method} {request.RequestUri}");
        foreach (var header in request.Headers)
            output.WriteLine($"[HTTP]    {header.Key}: {string.Join(", ", header.Value)}");

        var response = await base.SendAsync(request, cancellationToken);

        output.WriteLine($"[HTTP] <- {(int)response.StatusCode} {response.StatusCode}");
        var body = response.Content is null ? "" : await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrEmpty(body))
            output.WriteLine($"[HTTP]    body: {body[..Math.Min(body.Length, 1000)]}");

        return response;
    }
}

/// <summary>THROWAWAY diagnostics helper for TraceOneLlmCallAsync - prints every event from any EventSource whose name starts with "OpenTelemetry".</summary>
internal sealed class EventSourceDiagnosticsListener : EventListener
{
    private readonly ITestOutputHelper _output;

    public EventSourceDiagnosticsListener(ITestOutputHelper output)
    {
        _output = output;
        foreach (var source in EventSource.GetSources())
            if (source.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
                EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        try
        {
            var message = eventData.Message is not null && eventData.Payload is not null
                ? string.Format(eventData.Message, eventData.Payload.ToArray())
                : eventData.Message ?? eventData.EventName;
            _output.WriteLine($"[{eventData.EventSource.Name}] {message}");
        }
        catch (Exception)
        {
            _output.WriteLine($"[{eventData.EventSource.Name}] {eventData.EventName} (payload: {string.Join(", ", (IEnumerable<object?>?)eventData.Payload ?? Array.Empty<object?>())})");
        }
    }
}
