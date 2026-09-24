using System.Globalization;
using System.Text;
using System.Text.Json;
using Insights.Data;
using Insights.Domain;
using Insights.Worker.Integration;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// PREVIEW ONLY - the weekly insight JSON for one tenant, written to local files. Never runs
/// unless FreeDigest:InsightPreview:Enabled=true. The JSON lane's own preview: it shares nothing
/// with the email preview (FreeMonthlyPreviewWorker) beyond the recipient gate.
///
/// -- IT WRITES NOTHING, ANYWHERE -------------------------------------------------------------
/// No orchestration, no claim, no POST, no database write. Every SQL call is a read (the ONE
/// subject procedure that Sunday covers, per scope group). It spends real LLM tokens: one short
/// call per scope group, for the two lines of card text.
///
/// -- BUT IT FOLLOWS THE REAL PATH ------------------------------------------------------------
/// Same gate and scope grouping as FreeDigestInsightJsonOrchestrator (ResolveDigestRecipientsActivity
/// with the InsightJson claim domain), the same InsightCardComposer (the subject the email covers
/// that Sunday), and the exact body PostInsightJsonActivity would send (AiReportWeeklyMapper).
///
///   dotnet run -- --FreeDigest:InsightPreview:Enabled=true --FreeDigest:CustomerId=1082
///                 --Insights:ClientOnly=true --FreeDigest:Schedule:Enabled=false
///   optional:     --FreeDigest:InsightPreview:WeekEnding=2026-09-20   (a Sunday; default: the
///                     most recent Sunday on or before today)
///                 --FreeDigest:InsightPreview:Dir=C:\temp\insight     (default: ./insight-preview)
///                 --FreeDigest:InsightPreview:Groups=all   (default: 1 - the first scope group)
///                 --FreeDigest:UserId=14127   (one user instead of the resolved scope groups)
///                 --FreeDigest:InsightPreview:IgnoreClaims=true (preview even when the pipeline
///                     would exit because every recipient already holds this week's claim)
///                 --FreeDigest:InsightPreview:ReplayDir=C:\temp\captures  (compose from slot
///                     captures on disk instead of SQL - no database is touched at all, gate and
///                     grouping included, because both are SQL. The captured scope groups are the
///                     users the capture was taken for. Captures taken before a proc changed
///                     replay as they were: a grid added later reads as empty, never as an error.)
///
/// Writes, per scope group: {tenant}-g{n}-{weekEnding}.insight.json (the POST body) and
/// {tenant}-g{n}-{weekEnding}.insight.debug.txt (subject, title, metric, source, tokens, the JSON
/// the model was given, and its raw draft), plus {tenant}-insight-summary.txt.
/// </summary>
public sealed class InsightJsonPreviewWorker(
    IServiceProvider services,
    IConfiguration configuration,
    FreeDigestSettings settings,
    IHostApplicationLifetime lifetime,
    ILogger<InsightJsonPreviewWorker> logger) : BackgroundService
{
    /// <summary>Same encoder as PostInsightJsonActivity's dry-run log: readable apostrophes, indented for reading.</summary>
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("FreeDigest:InsightPreview:Enabled", false))
            return;

        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Insight JSON preview failed.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var tenantId = configuration.GetValue<int?>("FreeDigest:CustomerId")
                       ?? throw new InvalidOperationException("FreeDigest:CustomerId is required.");

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, settings.ScheduleTimeZone);
        var weekEnding = configuration["FreeDigest:InsightPreview:WeekEnding"] is { Length: > 0 } raw
            ? DateOnly.ParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : MostRecentSunday(DateOnly.FromDateTime(localNow));

        if (weekEnding.DayOfWeek != DayOfWeek.Sunday)
            throw new InvalidOperationException($"FreeDigest:InsightPreview:WeekEnding {weekEnding:yyyy-MM-dd} is a {weekEnding.DayOfWeek}, not a Sunday.");

        // The card reports as at the Sunday it would have been generated on; a future Sunday clamps to now.
        var asOf = weekEnding.ToDateTime(new TimeOnly(23, 59, 59)) <= localNow
            ? weekEnding.ToDateTime(new TimeOnly(23, 59, 59))
            : localNow;

        var dir = Path.GetFullPath(configuration["FreeDigest:InsightPreview:Dir"] ?? "insight-preview");
        Directory.CreateDirectory(dir);

        var summary = new StringBuilder();
        summary.AppendLine($"tenant {tenantId}, week ending {weekEnding:yyyy-MM-dd} (period_start_date {AiReportWeeklyMapper.PeriodStartDateFor(weekEnding):yyyy-MM-dd}), as at {asOf:s}");

        var groups = await ResolveGroupsAsync(sp, tenantId, summary, cancellationToken);

        /*  Replay composes from captures on disk, so the whole run is offline - the composer is
            built by hand around the captured repository rather than resolved, because DI's one is
            bound to SQL.                                                                        */
        var replayDir = ReplayDir();
        var composer = replayDir is null
            ? sp.GetRequiredService<InsightCardComposer>()
            : new InsightCardComposer(
                new CapturedFreeMonthlyDigestRepository(replayDir),
                sp.GetRequiredService<Insights.Agents.InsightCardWriter>(),
                sp.GetRequiredService<FreeMonthlySettings>(),
                settings,
                sp.GetRequiredService<ILogger<InsightCardComposer>>());

        logger.LogInformation(
            "Insight JSON preview: tenant {TenantId}, {GroupCount} scope group(s), week ending {WeekEnding}, output {Dir}. No email is composed; nothing is posted.",
            tenantId, groups.Count, weekEnding, dir);

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var stem = Path.Combine(dir, $"{tenantId}-g{g + 1}-{weekEnding:yyyyMMdd}");
            summary.AppendLine($"group {g + 1}: representative user {group.RepresentativeUserId}, {group.Recipients.Count} recipient(s)");

            try
            {
                var result = await composer.ComposeAsync(tenantId, group.RepresentativeUserId, weekEnding, asOf.ToString("s", CultureInfo.InvariantCulture), cancellationToken);
                var payload = AiReportWeeklyMapper.Map(
                    new PostInsightJsonInput(tenantId, group.RepresentativeUserId, weekEnding.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), result.Card),
                    AiReportWeeklyMapper.PeriodStartDateFor(weekEnding));

                await File.WriteAllTextAsync(stem + ".insight.json", JsonSerializer.Serialize(payload, Indented), cancellationToken);
                await File.WriteAllTextAsync(stem + ".insight.debug.txt", Debug(result), cancellationToken);

                summary.AppendLine($"  -> {result.Subject} ({result.Card.PrimaryMetric.Label}), {result.Source}, tokens {result.InputTokens} in / {result.OutputTokens} out");
                logger.LogInformation(
                    "Insight JSON preview group {Group}: {Subject} leads - {Source}{Reason} - tokens {In} in / {Out} out -> {File}.insight.json",
                    g + 1, result.Subject, result.Source, result.Reason is null ? "" : $" ({result.Reason})", result.InputTokens, result.OutputTokens, stem);
            }
            catch (FreeMonthlyDigestRefusedException ex)
            {
                await File.WriteAllTextAsync(stem + ".insight.debug.txt",
                    $"REFUSED by SQL error {ex.SqlErrorNumber} - in production nothing is posted for this scope group.\n\n{ex.Message}", cancellationToken);
                summary.AppendLine($"  -> REFUSED (SQL {ex.SqlErrorNumber})");
                logger.LogError("Insight JSON preview group {Group}: REFUSED by SQL error {Code} - see {File}.insight.debug.txt", g + 1, ex.SqlErrorNumber, stem);
            }
        }

        await File.WriteAllTextAsync(Path.Combine(dir, $"{tenantId}-insight-summary.txt"), summary.ToString(), cancellationToken);
    }

    private static string Debug(InsightCardResult result)
    {
        var c = result.Card;
        var sb = new StringBuilder();
        sb.AppendLine($"SUBJECT: {result.Subject} (the same one this Sunday's email covers)  type {c.Type}  severity {c.Severity}");
        sb.AppendLine($"TITLE: {c.Title}");
        sb.AppendLine($"METRIC: {c.PrimaryMetric.Current} {c.PrimaryMetric.Unit} - {c.PrimaryMetric.Label} (target {c.PrimaryMetric.Target}, {c.PrimaryMetric.Direction})");
        sb.AppendLine($"SOURCE: {result.Source}{(result.Reason is null ? "" : $" ({result.Reason})")}   tokens {result.InputTokens} in / {result.OutputTokens} out");
        sb.AppendLine();
        sb.AppendLine("== MODEL INPUT ==");
        sb.AppendLine(result.UserMessage);
        sb.AppendLine();
        sb.AppendLine("== MODEL RAW DRAFT ==");
        sb.AppendLine(result.RawDraft);
        return sb.ToString();
    }

    private string? ReplayDir() =>
        configuration["FreeDigest:InsightPreview:ReplayDir"] is { Length: > 0 } dir ? Path.GetFullPath(dir) : null;

    private async Task<IReadOnlyList<DigestScopeGroup>> ResolveGroupsAsync(IServiceProvider sp, int tenantId, StringBuilder summary, CancellationToken cancellationToken)
    {
        /*  A replay must work with NO database at all - that is the point of capturing. Gate,
            recipients and grouping are all SQL, so the groups come from the capture files: one per
            representative user captured for this tenant. The real grouping already happened when
            the capture was taken.                                                               */
        if (ReplayDir() is { } replayDir)
        {
            var captured = CapturedFreeMonthlyDigestStore.CapturedUserIds(replayDir, tenantId);
            if (captured.Count == 0)
                throw new InvalidOperationException(
                    $"No captures for tenant {tenantId} in {replayDir}. Capture it first with --FreeDigest:Preview:CaptureDir.");

            summary.AppendLine($"REPLAY from {replayDir}: {captured.Count} captured scope group(s), no database used.");

            var forced = configuration.GetValue<int?>("FreeDigest:UserId");
            var chosen = forced is { } f
                ? captured.Contains(f)
                    ? [f]
                    : throw new InvalidOperationException($"Tenant {tenantId} has no capture for user {f}. Captured: {string.Join(", ", captured)}.")
                : string.Equals(configuration["FreeDigest:InsightPreview:Groups"], "all", StringComparison.OrdinalIgnoreCase)
                    ? captured
                    : captured.Take(1).ToList();

            return chosen.Select(u => new DigestScopeGroup($"replay-u{u}", u, [])).ToList();
        }

        var digestRepository = sp.GetRequiredService<IFreeDigestRepository>();

        var gate = await digestRepository.EvaluateGateAsync(tenantId, cancellationToken);
        summary.AppendLine($"gate: {gate.Decision} - {gate.Reason} (recipients: {gate.RecipientCount})");

        var resolve = new ResolveDigestRecipientsActivity(
            digestRepository,
            sp.GetRequiredService<IInsightJsonRepository>(),
            sp.GetRequiredService<IScopeRepository>(),
            sp.GetRequiredService<FreeDigestMetrics>(),
            sp.GetRequiredService<ILogger<ResolveDigestRecipientsActivity>>());

        var resolved = await resolve.RunAsync(new ResolveDigestRecipientsInput(tenantId, null, DigestClaimDomain.InsightJson));
        summary.AppendLine($"resolve: proceed={resolved.ShouldProceed}, decision={resolved.Decision}, "
                           + $"{resolved.Groups.Count} scope group(s), {resolved.RecipientsWithoutScope} recipient(s) dropped with no scope");

        var forcedUser = configuration.GetValue<int?>("FreeDigest:UserId");
        if (resolved.ShouldProceed && resolved.Groups.Count > 0)
        {
            if (forcedUser is { } user)
                return [resolved.Groups.FirstOrDefault(g => g.RepresentativeUserId == user) ?? new DigestScopeGroup("forced-user", user, [])];

            // COST CONTROL: every group is a set of five proc reads and one LLM call. First group by default.
            var limit = configuration["FreeDigest:InsightPreview:Groups"];
            var take = string.Equals(limit, "all", StringComparison.OrdinalIgnoreCase)
                ? resolved.Groups.Count
                : int.TryParse(limit, out var n) && n > 0 ? n : 1;
            return resolved.Groups.Take(take).ToList();
        }

        if (!configuration.GetValue("FreeDigest:InsightPreview:IgnoreClaims", false))
            throw new InvalidOperationException(
                $"The pipeline would post nothing for tenant {tenantId}: {resolved.Decision} - {resolved.Reason}. "
                + "Pass --FreeDigest:InsightPreview:IgnoreClaims=true to preview anyway.");

        summary.AppendLine("IgnoreClaims: the pipeline would have exited here; previewing the first recipient with scope instead.");
        var userId = forcedUser ?? await FirstScopedRecipientAsync(sp, digestRepository, tenantId, cancellationToken);
        return [new DigestScopeGroup("preview", userId, [])];
    }

    private static async Task<int> FirstScopedRecipientAsync(IServiceProvider sp, IFreeDigestRepository repository, int tenantId, CancellationToken cancellationToken)
    {
        var scopeRepository = sp.GetRequiredService<IScopeRepository>();
        foreach (var recipient in await repository.GetRecipientsAsync(tenantId, cancellationToken))
        {
            var id = checked((int)recipient.UserId);
            if ((await scopeRepository.GetScopePairsAsync(id, tenantId, cancellationToken)).Count > 0)
                return id;
        }

        throw new InvalidOperationException($"Tenant {tenantId} has no digest recipient with scope - pass --FreeDigest:UserId.");
    }

    private static DateOnly MostRecentSunday(DateOnly today) =>
        today.AddDays(-(((int)today.DayOfWeek - (int)DayOfWeek.Sunday + 7) % 7));
}
