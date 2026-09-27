using System.Globalization;
using System.Text.Json;
using Insights.Agents;
using Insights.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// [ADDED 2026-09-27] Proves every testers' database check in source-maps/*.json returns EXACTLY
/// what the dimension's own procedure returned for the same tenant, user and period - so a tester
/// is never handed a query that disagrees with the report. Re-run whenever a dimension's SQL or its
/// source map changes. Hits real UAT SQL (no LLM):
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~SourceMapVerificationTests
/// </summary>
public sealed class SourceMapVerificationTests(ITestOutputHelper output)
{

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddJsonFile(@"D:\trpl-reginsights-dev\appsettings.uat.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    private static string ConnectionString =>
        Config["ConnectionStrings:RegTrack"] ?? throw new InvalidOperationException("Set ConnectionStrings:RegTrack in appsettings.uat.json.");

    private static (DateTime Start, DateTime End) Last30Days()
    {
        var end = DateTime.UtcNow.Date.AddDays(1);
        return (end.AddDays(-30), end);
    }

    /// <summary>
    /// Different profiles (CLAUDE.md: never validate on one tenant): 1285 has real prison-term
    /// exposure, 5 has many statuses and the most recent activity on UAT, 29 has nothing due in the
    /// last 30 days (the empty-population boundary).
    /// </summary>
    public static IEnumerable<object[]> Tenants => [[1285, 11416], [5, 38], [29, 38]];

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Users_EveryCheckMatchesTheProcedure(int tenantId, int userId)
    {
        var (ws, we) = Last30Days();
        var result = await new SqlDimensionRepository(ConnectionString).GetUsersAsync(userId, tenantId, ws, we);
        await VerifyAsync("Users", tenantId, userId, ws, we,
            JsonSerializer.SerializeToElement(result.ControlTotals), JsonSerializer.SerializeToElement(result.Rows),
            JsonSerializer.SerializeToElement(result.Assertions), rowSample: 25);
    }

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Act_EveryCheckMatchesTheProcedure(int tenantId, int userId)
    {
        var (ws, we) = Last30Days();
        var result = await new SqlDimensionRepository(ConnectionString).GetActAsync(userId, tenantId, ws, we);
        await VerifyAsync("Act", tenantId, userId, ws, we,
            JsonSerializer.SerializeToElement(result.ControlTotals), JsonSerializer.SerializeToElement(result.Rows),
            JsonSerializer.SerializeToElement(result.Assertions), rowSample: int.MaxValue);
    }

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Risk_EveryCheckMatchesTheProcedure(int tenantId, int userId)
    {
        var (ws, we) = Last30Days();
        var result = await new SqlDimensionRepository(ConnectionString).GetRiskAsync(userId, tenantId, ws, we);
        await VerifyAsync("Risk", tenantId, userId, ws, we,
            JsonSerializer.SerializeToElement(result.ControlTotals), JsonSerializer.SerializeToElement(result.Rows),
            JsonSerializer.SerializeToElement(result.Assertions), rowSample: int.MaxValue);
    }

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Nature_EveryCheckMatchesTheProcedure(int tenantId, int userId)
    {
        var (ws, we) = Last30Days();
        var result = await new SqlDimensionRepository(ConnectionString).GetNatureAsync(userId, tenantId, ws, we);
        await VerifyAsync("Nature", tenantId, userId, ws, we,
            JsonSerializer.SerializeToElement(result.ControlTotals), JsonSerializer.SerializeToElement(result.Rows),
            JsonSerializer.SerializeToElement(result.Assertions), rowSample: 20);
    }

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Departments_EveryCheckMatchesTheProcedure(int tenantId, int userId)
    {
        var (ws, we) = Last30Days();
        var result = await new SqlDimensionRepository(ConnectionString).GetDepartmentsAsync(userId, tenantId, ws, we);
        await VerifyAsync("Departments", tenantId, userId, ws, we,
            JsonSerializer.SerializeToElement(result.ControlTotals), JsonSerializer.SerializeToElement(result.Rows),
            JsonSerializer.SerializeToElement(result.Assertions), rowSample: 20);
    }

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Internal_EveryCheckMatchesTheProcedure(int tenantId, int userId)
    {
        var (ws, we) = Last30Days();
        var result = await new SqlDimensionRepository(ConnectionString).GetInternalAsync(userId, tenantId, ws, we);
        await VerifyAsync("Internal", tenantId, userId, ws, we,
            JsonSerializer.SerializeToElement(result.ControlTotals), JsonSerializer.SerializeToElement(result.Rows),
            JsonSerializer.SerializeToElement(result.Assertions), rowSample: 20);
    }

    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task Location_EveryCheckMatchesTheProcedure(int tenantId, int userId)
    {
        var (ws, we) = Last30Days();
        var result = await new SqlDimensionRepository(ConnectionString).GetLocationAsync(userId, tenantId, ws, we);
        await VerifyAsync("Location", tenantId, userId, ws, we,
            JsonSerializer.SerializeToElement(result.ControlTotals), JsonSerializer.SerializeToElement(result.Rows),
            JsonSerializer.SerializeToElement(result.Assertions), rowSample: 20);
    }

    /// <summary>
    /// Licence has no report period - it counts as of today. Tenant 5 is swapped for 1355: on UAT
    /// tenant 5's own Licence procedure refuses (a licence points to a type missing from the master -
    /// the proc's deliberate fail-closed referential check), so there is nothing to compare against.
    /// </summary>
    [Theory]
    [InlineData(1285, 11416)]
    [InlineData(29, 38)]
    [InlineData(1355, 11885)]
    public async Task Licence_EveryCheckMatchesTheProcedure(int tenantId, int userId)
    {
        var result = await new SqlDimensionRepository(ConnectionString).GetLicenceAsync(userId, tenantId);
        await VerifyAsync("Licence", tenantId, userId, DateTime.MinValue, DateTime.MinValue,
            JsonSerializer.SerializeToElement(result.ControlTotals), JsonSerializer.SerializeToElement(result.Rows),
            JsonSerializer.SerializeToElement(result.Assertions), rowSample: 20);
    }

    /// <summary>BacklogAging has no report period - it counts overdue due dates as of today.</summary>
    [Theory]
    [MemberData(nameof(Tenants))]
    public async Task BacklogAging_EveryCheckMatchesTheProcedure(int tenantId, int userId)
    {
        var result = await new SqlDimensionRepository(ConnectionString).GetBacklogAgingAsync(userId, tenantId);
        await VerifyAsync("BacklogAging", tenantId, userId, DateTime.MinValue, DateTime.MinValue,
            JsonSerializer.SerializeToElement(result.ControlTotals), JsonSerializer.SerializeToElement(result.Rows),
            JsonSerializer.SerializeToElement(result.Assertions), rowSample: int.MaxValue);
    }

    /// <summary>Events are rare in a 30-day window on UAT, so a full year is checked too - otherwise the per-type checks never run.</summary>
    [Theory]
    [InlineData(1285, 11416, 30)]
    [InlineData(5, 38, 30)]
    [InlineData(29, 38, 30)]
    [InlineData(1285, 11416, 365)]
    [InlineData(5, 38, 365)]
    [InlineData(29, 38, 365)]
    public async Task Event_EveryCheckMatchesTheProcedure(int tenantId, int userId, int days)
    {
        var we = DateTime.UtcNow.Date.AddDays(1);
        var ws = we.AddDays(-days);
        var result = await new SqlDimensionRepository(ConnectionString).GetEventAsync(userId, tenantId, ws, we);
        await VerifyAsync("Event", tenantId, userId, ws, we,
            JsonSerializer.SerializeToElement(result.ControlTotals), JsonSerializer.SerializeToElement(result.Rows),
            JsonSerializer.SerializeToElement(result.Assertions), rowSample: 20);
    }

    private async Task VerifyAsync(string dimension, int tenantId, int userId, DateTime ws, DateTime we, JsonElement totals, JsonElement rows, JsonElement assertions, int rowSample)
    {
        var map = ReasoningSourceMap.Load(dimension) ?? throw new InvalidOperationException($"No source map for {dimension}.");
        var rowList = rows.EnumerateArray().ToList();
        var assertionList = assertions.EnumerateArray().ToList();
        output.WriteLine($"[{dimension} t{tenantId}] {rowList.Count} rows, {assertionList.Count} assertions, window {ws:yyyy-MM-dd}..{we:yyyy-MM-dd}");

        // Biggest rows first plus a spread through the rest - small and zero rows are where mistakes hide.
        var sampled = rowList.Count <= rowSample ? rowList
            : rowList.Take(rowSample / 2).Concat(rowList.Skip(rowSample / 2).Where((_, i) => i % Math.Max(1, (rowList.Count - rowSample / 2) / (rowSample - rowSample / 2)) == 0).Take(rowSample - rowSample / 2)).ToList();

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using (var setup = new SqlCommand(ReasoningSourceMap.Fill(map.SetupText, userId, tenantId, ws, we), conn) { CommandTimeout = 300 })
            await setup.ExecuteNonQueryAsync();

        var failures = new List<string>();
        var passed = 0;
        var skipped = new List<string>();

        foreach (var check in map.Checks)
        {
            var parts = check.Compare.Split('|');
            switch (parts[0])
            {
                case "totals":
                    Compare(check.Id, parts[1], Prop(totals, parts[1]), await ScalarAsync(conn, check.QueryText));
                    break;

                case "row":
                    foreach (var row in sampled)
                    {
                        var key = KeyText(Prop(row, map.RowKeyField)!.Value);
                        Compare($"{check.Id}[{map.RowKeyField}={key}]", parts[1], Prop(row, parts[1]),
                            await ScalarAsync(conn, check.QueryText.Replace("{RowKey}", key)));
                    }
                    break;

                case "assertion":
                {
                    var a = assertionList.FirstOrDefault(x => Prop(x, "AssertionId")?.GetString() == parts[1]);
                    if (a.ValueKind == JsonValueKind.Undefined) { skipped.Add($"{check.Id} ({parts[1]} not emitted this run)"); break; }
                    Compare(check.Id, $"{parts[1]}.{parts[2]}", Prop(a, parts[2]), await ScalarAsync(conn, check.QueryText));
                    break;
                }

                case "rowNamedByAssertion":
                {
                    // The per-row query, run for the row the assertion names (ScopeLabel = the row's name).
                    var a = assertionList.FirstOrDefault(x => Prop(x, "AssertionId")?.GetString() == parts[1]);
                    if (a.ValueKind == JsonValueKind.Undefined) { skipped.Add($"{check.Id} ({parts[1]} not emitted this run)"); break; }
                    var name = Prop(a, "ScopeLabel")?.GetString();
                    var row = rowList.FirstOrDefault(r => Prop(r, map.RowNameField)?.GetString() == name);
                    if (row.ValueKind == JsonValueKind.Undefined) { failures.Add($"{check.Id}: no row named '{name}'"); break; }
                    var key = KeyText(Prop(row, map.RowKeyField)!.Value);
                    Compare($"{check.Id}[{map.RowKeyField}={key}]", $"{parts[1]}.{parts[2]}", Prop(a, parts[2]),
                        await ScalarAsync(conn, check.QueryText.Replace("{RowKey}", key)));
                    break;
                }

                case "assertionsByScope":
                {
                    var expectedByScope = assertionList
                        .Where(x => Prop(x, "Metric")?.GetString() == parts[1] && (Prop(x, "ScopeLabel")?.GetString() ?? "").StartsWith(parts[2], StringComparison.Ordinal))
                        .ToDictionary(x => Prop(x, "ScopeLabel")!.Value.GetString()![parts[2].Length..], x => x);
                    var actual = await KeyValueAsync(conn, check.QueryText);
                    foreach (var (scope, a) in expectedByScope)
                        Compare($"{check.Id}[{scope}]", $"{parts[1]}.{parts[3]}", Prop(a, parts[3]), actual.GetValueOrDefault(scope));
                    foreach (var extra in actual.Keys.Except(expectedByScope.Keys))
                        failures.Add($"{check.Id}: query returned group '{extra}' that the report does not have");
                    break;
                }

                case "rowsCount":
                {
                    var target = decimal.Parse(parts[3], CultureInfo.InvariantCulture);
                    var expected = rowList.Count(r => Prop(r, parts[1]) is { ValueKind: JsonValueKind.Number } v
                        && (parts[2] == "eq" ? v.GetDecimal() == target : v.GetDecimal() > target));
                    Compare(check.Id, check.Compare, JsonSerializer.SerializeToElement(expected), await ScalarAsync(conn, check.QueryText));
                    break;
                }

                case "rowsFlagCount":
                {
                    var expected = rowList.Count(r => (Prop(r, "Flags")?.GetString() ?? "").Contains(parts[1], StringComparison.Ordinal));
                    Compare(check.Id, check.Compare, JsonSerializer.SerializeToElement(expected), await ScalarAsync(conn, check.QueryText));
                    break;
                }

                default:
                    failures.Add($"{check.Id}: unknown compare kind '{parts[0]}'");
                    break;
            }
        }

        output.WriteLine($"[{dimension} t{tenantId}] passed {passed} comparisons, {failures.Count} failed, {skipped.Count} skipped");
        foreach (var s in skipped) output.WriteLine($"  skipped: {s}");
        foreach (var f in failures) output.WriteLine($"  FAIL: {f}");
        Assert.Empty(failures);

        void Compare(string where, string what, JsonElement? expected, object? actual)
        {
            var e = expected is { ValueKind: JsonValueKind.Number } n ? n.GetDecimal() : (decimal?)null;
            var a = actual is null or DBNull ? (decimal?)null : Convert.ToDecimal(actual, CultureInfo.InvariantCulture);
            if (e == a) passed++;
            else failures.Add($"{where} {what}: report={e?.ToString(CultureInfo.InvariantCulture) ?? "null"} query={a?.ToString(CultureInfo.InvariantCulture) ?? "null"}");
        }
    }

    /// <summary>A text key (BacklogAging's bucket) goes in bare - the query already quotes '{RowKey}'.</summary>
    private static string KeyText(JsonElement key) => key.ValueKind == JsonValueKind.String ? key.GetString()! : key.GetRawText();

    private static JsonElement? Prop(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static async Task<object?> ScalarAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<Dictionary<string, object?>> KeyValueAsync(SqlConnection conn, string sql)
    {
        var result = new Dictionary<string, object?>();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[Convert.ToString(reader["Key"], CultureInfo.InvariantCulture)!] = reader["Value"];
        return result;
    }
}
