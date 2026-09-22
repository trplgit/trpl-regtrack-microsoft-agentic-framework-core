using System.Text.Json;
using System.Text.Json.Serialization;
using Insights.Domain;

namespace Insights.Data;

/// <summary>
/// One slot proc's output, captured to disk so prompt work can continue without the database.
/// A refusal is captured too - replaying it must fail closed exactly as SQL did, or the replay
/// would quietly "fix" a tenant whose proc actually throws.
/// </summary>
public sealed record CapturedSlot(
    int CustomerId,
    int UserId,
    MonthlyDigestSlot Slot,
    DateTime AsOf,
    bool AllowPersonNames,
    string CapturedAtUtc,
    MonthlyDigestData? Data,
    int? RefusedSqlErrorNumber,
    string? RefusedMessage);

/// <summary>
/// Reads slot data captured by <see cref="CapturedFreeMonthlyDigestStore"/> instead of calling SQL.
///
/// <para>The UAT database is not always available, and the procs are the slow part of a preview run.
/// Capturing once and replaying lets prompts and the model be tuned offline against the same real
/// tenant data, with no risk of touching a live database while iterating.</para>
///
/// <para>It is a DEVELOPMENT path: <c>FreeDigest:Preview:ReplayDir</c> selects it, and only while
/// <c>FreeDigest:Preview:Enabled</c> is true. Nothing in the scheduled pipeline uses it.</para>
/// </summary>
public sealed class CapturedFreeMonthlyDigestRepository(string directory) : IFreeMonthlyDigestRepository
{
    public Task<MonthlyDigestData> GetSlotAsync(
        MonthlyDigestEdition edition, int customerId, int userId, DateTime asOf, bool allowPersonNames,
        CancellationToken cancellationToken = default)
    {
        var path = CapturedFreeMonthlyDigestStore.PathFor(directory, customerId, userId, edition.Slot);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"No captured data for tenant {customerId}, user {userId}, slot {edition.Slot}. "
                + $"Capture it first with --FreeDigest:Preview:Capture=true, or drop the replay directory.", path);

        var captured = JsonSerializer.Deserialize<CapturedSlot>(File.ReadAllText(path), CapturedFreeMonthlyDigestStore.Json)
                       ?? throw new InvalidOperationException($"{path} is not a captured slot.");

        if (captured.RefusedSqlErrorNumber is { } code)
            throw new FreeMonthlyDigestRefusedException(code, captured.RefusedMessage ?? "captured refusal", new InvalidOperationException("replayed from capture"));

        var data = captured.Data ?? throw new InvalidOperationException($"{path} has neither data nor a refusal.");

        /*  The capture carries the AsOf it was taken at. Re-dating it here would leave every "as at"
            line in the email disagreeing with the figures behind it, so the captured moment wins and
            the caller's AsOf is ignored - a replay reproduces that run, it does not simulate a new one. */
        return Task.FromResult(data);
    }
}

/// <summary>Writes and locates the capture files. Shared by the capture pass and the replay repository.</summary>
public static class CapturedFreeMonthlyDigestStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathFor(string directory, int customerId, int userId, MonthlyDigestSlot slot) =>
        Path.Combine(directory, $"{customerId}-u{userId}-{slot.ToString().ToLowerInvariant()}.json");

    /// <summary>
    /// The representative users captured for a tenant, in file order - the preview's scope groups
    /// when replaying, since gate and grouping are SQL and a replay must not touch the database.
    /// </summary>
    public static IReadOnlyList<int> CapturedUserIds(string directory, int customerId)
    {
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, $"{customerId}-u*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f).Split('-'))
            .Where(parts => parts.Length >= 2 && parts[1].StartsWith('u'))
            .Select(parts => int.TryParse(parts[1][1..], out var id) ? id : (int?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .Order()
            .ToList();
    }

    public static string Write(string directory, CapturedSlot captured)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(directory, captured.CustomerId, captured.UserId, captured.Slot);
        File.WriteAllText(path, JsonSerializer.Serialize(captured, Json));
        return path;
    }
}
