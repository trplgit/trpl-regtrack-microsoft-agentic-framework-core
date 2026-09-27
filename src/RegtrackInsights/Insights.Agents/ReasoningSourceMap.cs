using System.Globalization;
using System.Text.Json;

namespace Insights.Agents;

public sealed record SourceMapCheck(string Id, string Name, string Compare, string Tables, IReadOnlyList<string> Query)
{
    public string QueryText => string.Join("\n", Query);
}

public sealed record SourceMapFile(
    string Dimension, string RowKeyField, string RowNameField, string Note,
    IReadOnlyList<string> Setup, IReadOnlyList<SourceMapCheck> Checks)
{
    public string SetupText => string.Join("\n", Setup);
}

/// <summary>
/// [ADDED 2026-09-27] Testers' database checks for one dimension: a setup script that rebuilds the
/// report's exact population (same scope and overdue functions the procedure uses) plus one short
/// query per number. Written by hand from the dimension's SQL - never by a model - and verified by
/// SourceMapVerificationTests to return exactly what the procedure returned. Files live in
/// source-maps/{dimension}.json; a dimension without a file simply gets no database checks.
/// </summary>
public static class ReasoningSourceMap
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static SourceMapFile? Load(string dimension, string? baseDirectory = null)
    {
        var path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "source-maps", dimension.ToLowerInvariant() + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SourceMapFile>(File.ReadAllText(path), ReadOptions) : null;
    }

    /// <summary>Fills {UserID}, {CustomerID}, {WindowStart}, {WindowEnd}. {RowKey} is left for the row being checked.</summary>
    public static string Fill(string sql, int userId, int customerId, DateTime windowStart, DateTime windowEnd) =>
        sql.Replace("{UserID}", userId.ToString(CultureInfo.InvariantCulture))
           .Replace("{CustomerID}", customerId.ToString(CultureInfo.InvariantCulture))
           .Replace("{WindowStart}", windowStart.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
           .Replace("{WindowEnd}", windowEnd.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

    /// <summary>
    /// The filled map as compact JSON for the explainer, or null when there is no map, or the map needs
    /// a report period and the report has none. Licence and BacklogAging are counted as of today, so
    /// their maps carry no period placeholders and load without one.
    /// </summary>
    public static string? LoadFilledJson(string dimension, int userId, int customerId, DateTime? windowStart, DateTime? windowEnd, string? baseDirectory = null)
    {
        var map = Load(dimension, baseDirectory);
        if (map is null)
            return null;
        var needsPeriod = map.SetupText.Contains("{WindowStart}", StringComparison.Ordinal) || map.SetupText.Contains("{WindowEnd}", StringComparison.Ordinal);
        if (needsPeriod && (windowStart is null || windowEnd is null))
            return null;
        var ws = windowStart ?? DateTime.MinValue;
        var we = windowEnd ?? DateTime.MinValue;

        return JsonSerializer.Serialize(new
        {
            row_key_field = map.RowKeyField,
            row_name_field = map.RowNameField,
            note = map.Note,
            setup_sql = Fill(map.SetupText, userId, customerId, ws, we),
            checks = map.Checks.Select(c => new
            {
                id = c.Id,
                name = c.Name,
                tables = c.Tables,
                per_row = c.Compare.StartsWith("row|", StringComparison.Ordinal),
                query_sql = c.QueryText,
            }),
        });
    }
}
