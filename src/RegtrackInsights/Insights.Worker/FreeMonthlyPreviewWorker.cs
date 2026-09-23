using System.Text.Json;
using System.Globalization;
using System.Text;
using Insights.Data;
using Insights.Domain;
using Insights.Presentation;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// PREVIEW ONLY - renders one tenant's digest emails to local files, for reading the output and
/// tuning prompts. Never runs unless FreeDigest:Preview:Enabled=true.
///
/// -- IT WRITES NOTHING, ANYWHERE -------------------------------------------------------------
/// No Durable Task orchestration (so no deployed worker on the shared hub can pick the job up and
/// run it with different code), no artifact-slot claim, no per-recipient send claim, no blob, no
/// database write of any kind, no email. Every SQL call it makes is a read. The ONLY output is the
/// files it writes to the preview folder. It does spend real LLM tokens - one call per email.
///
/// -- BUT IT FOLLOWS THE REAL PATH ------------------------------------------------------------
/// Same entitlement gate, recipient list, unsubscribe filter and scope grouping as the pipeline
/// (ResolveDigestRecipientsActivity, unchanged), so it produces ONE email per scope group exactly
/// as production would; the same slot procs, prompts, validator, binder, fallback, HTML shell and
/// subject line; and an in-memory encrypt/decrypt round trip of the finished HTML, which is what
/// the artifact store does before it writes a blob.
///
/// -- WHAT ONLY A REAL RUN CAN PROVE ----------------------------------------------------------
/// The claims (at-most-once per recipient per week), the artifact slot, the blob write/read-back,
/// Monday's dispatch, and Durable Task's retry/replay behaviour. None of that code was changed by
/// the monthly work.
///
///   dotnet run -- --FreeDigest:Preview:Enabled=true --FreeDigest:CustomerId=5
///                 --Insights:ClientOnly=true --FreeDigest:Schedule:Enabled=false
///   optional:     --FreeDigest:Preview:Slots=overview,users   (default: all five)
///                 --FreeDigest:Preview:Month=2026-09    (default: the current month)
///                 --FreeDigest:Preview:Dir=C:\temp\monthly   (default: ./monthly-preview)
///                 --FreeDigest:Preview:IgnoreClaims=true (preview even when everyone in the
///                     tenant already holds this week's send claim - the real pipeline would exit)
///                 --FreeDigest:Preview:Groups=all   (default: 1 - only the first scope group,
///                     because every group costs a full set of LLM calls)
///                 --FreeDigest:UserId=36   (one user instead of the resolved scope groups)
///                 --FreeDigest:Preview:CaptureDir=C:\temp\captures  (run the procs and save their
///                   output to disk, making NO LLM call - so prompt work can continue when the UAT
///                   database is gone; use Groups=all to capture every scope group)
///                 --FreeDigest:Preview:ReplayDir=C:\temp\captures   (compose from those captures
///                   instead of SQL - no database connection is used for the slot data)
///
/// Writes, per scope group and email: {tenant}-g{n}-{slot}.html (as the recipient would see it)
/// and {tenant}-g{n}-{slot}.debug.txt (subject, source, validator verdict, tokens, every fact and
/// candidate the proc returned, the JSON the model was given, and the model's raw draft), plus
/// {tenant}-summary.txt (gate decision, groups, recipients, claim status).
/// </summary>
public sealed class FreeMonthlyPreviewWorker(
    IServiceProvider services,
    IConfiguration configuration,
    FreeDigestSettings settings,
    IHostApplicationLifetime lifetime,
    ILogger<FreeMonthlyPreviewWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("FreeDigest:Preview:Enabled", false))
            return;

        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Monthly preview failed.");
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

        var digestRepository = sp.GetRequiredService<IFreeDigestRepository>();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, settings.ScheduleTimeZone);

        var monthStart = configuration["FreeDigest:Preview:Month"] is { Length: > 0 } rawMonth
            ? DateOnly.ParseExact(rawMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : new DateOnly(localNow.Year, localNow.Month, 1);

        var slots = (configuration["FreeDigest:Preview:Slots"] ?? "overview,users,location,act,licence")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.Parse<MonthlyDigestSlot>(s, ignoreCase: true))
            .Distinct()
            .ToList();

        var dir = Path.GetFullPath(configuration["FreeDigest:Preview:Dir"] ?? "monthly-preview");
        Directory.CreateDirectory(dir);

        var summary = new StringBuilder();
        var groups = await ResolveGroupsAsync(sp, digestRepository, tenantId, summary, cancellationToken);

        /*  The tenant's display name, in order of trust: what the capture recorded alongside the
            data, then an explicit override, then the entitled-tenant list, then the id.

            [FOUND LIVE on tenant 1082, 2026-09-22] The masthead read "Tenant 1082". Two separate
            reasons, both fixed here: a replay has no database and fell straight through to the id
            unless --TenantName was passed, and the live lookup goes through GetEntitledTenantsAsync,
            which filters on ProductMapping - so ANY tenant previewed without an entitlement row,
            which is every production tenant we capture, resolved to its id as well.            */
        /*  [CORRECTED 2026-09-22] An EXPLICIT override wins, always. This had the captured name
            first, so an August capture that recorded "Tenant 1082" (the lookup fails for any
            unentitled tenant, which is every production one) overrode the --TenantName="Agrocel"
            passed on the replay, and the masthead read "Tenant 1082" anyway. A flag the operator
            typed is the most specific thing in the room; it cannot be outranked by a fallback.  */
        var replayDir = configuration["FreeDigest:Preview:ReplayDir"];
        var tenantName =
            configuration["FreeDigest:Preview:TenantName"]
            ?? (replayDir is { Length: > 0 } ? CapturedTenantName(replayDir, tenantId) : null)
            ?? (replayDir is { Length: > 0 }
                ? null
                : (await digestRepository.GetEntitledTenantsAsync(cancellationToken))
                    .FirstOrDefault(t => t.CustomerId == tenantId)?.TenantName)
            ?? $"Tenant {tenantId}";

        var composer = sp.GetRequiredService<FreeMonthlyDigestComposer>();
        var renderer = sp.GetRequiredService<FreeDigestEmailRenderer>();
        var monthly = sp.GetRequiredService<FreeMonthlySettings>();

        /*  Capture mode: run every slot proc for every scope group and write the result to disk,
            making no LLM call at all. The captures replay through CapturedFreeMonthlyDigestRepository
            (FreeDigest:Preview:ReplayDir), so prompt work survives the UAT database going away.   */
        if (configuration["FreeDigest:Preview:CaptureDir"] is { Length: > 0 } captureDir)
        {
            await CaptureAsync(sp, Path.GetFullPath(captureDir), tenantId, tenantName, groups, slots, monthStart, localNow, monthly, cancellationToken);
            return;
        }

        logger.LogInformation(
            "Monthly preview: tenant {TenantId} ({TenantName}), {GroupCount} scope group(s), month {Month:yyyy-MM}, slots {Slots}, names {Names}, output {Dir}.",
            tenantId, tenantName, groups.Count, monthStart, string.Join(",", slots), monthly.AllowPersonNames ? "shown" : "withheld", dir);

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            summary.AppendLine($"group {g + 1}: representative user {group.RepresentativeUserId}, {group.Recipients.Count} recipient(s): "
                               + string.Join(", ", group.Recipients.Select(r => $"{r.UserId} {r.Email}")));

            foreach (var slot in slots)
            {
                var edition = EditionFor(slot, monthStart);
                var stem = Path.Combine(dir, $"{tenantId}-g{g + 1}-{(int)slot + 1}-{slot.ToString().ToLowerInvariant()}");

                // AsOf = now when previewing this month; the month's last moment when previewing a past one.
                var asOf = AsOfFor(edition, localNow).ToString("s", CultureInfo.InvariantCulture);

                try
                {
                    var result = await composer.ComposeWithDiagnosticsAsync(tenantId, group.RepresentativeUserId, edition, asOf, cancellationToken);

                    var html = await renderer.RenderHtmlAsync(
                        result.Output.Body, tenantName, edition, settings.UpgradeUrl, "#", settings.PortalUrl, cancellationToken);

                    var subject = SendDigestFromArtifactActivity.BuildSubject(tenantName, edition);
                    // The codec check needs Key Vault and SQL; a replay is deliberately offline.
                    var codec = configuration["FreeDigest:Preview:ReplayDir"] is { Length: > 0 }
                        ? "not checked (replay)"
                        : await CodecRoundTripAsync(sp, html, cancellationToken);

                    await File.WriteAllTextAsync(stem + ".html", html, cancellationToken);
                    await File.WriteAllTextAsync(stem + ".debug.txt", Debug(result, edition, tenantId, group.RepresentativeUserId, subject, codec), cancellationToken);

                    logger.LogInformation(
                        "Monthly preview group {Group} {Slot}: {Source}{Reason} - tokens {In} in / {Out} out - {Codec} -> {File}.html",
                        g + 1, slot, result.Output.Source, result.Output.Reason is null ? "" : $" ({result.Output.Reason})",
                        result.Output.InputTokens, result.Output.OutputTokens, codec, stem);

                    /*  One row per email, appended across runs, so model choice can be costed from
                        measurement rather than argued from list prices. The deployment and its
                        knobs are on every row: a run is only comparable to another if those match. */
                    await AppendCostRowAsync(dir, tenantId, group.RepresentativeUserId, slot, result.Output, cancellationToken);
                }
                catch (FreeMonthlyDigestRefusedException ex)
                {
                    await File.WriteAllTextAsync(stem + ".debug.txt",
                        $"REFUSED by SQL error {ex.SqlErrorNumber} - in production nothing is sent to this scope group.\n\n{ex.Message}", cancellationToken);
                    logger.LogError("Monthly preview group {Group} {Slot}: REFUSED by SQL error {Code} - see {File}.debug.txt", g + 1, slot, ex.SqlErrorNumber, stem);
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(dir, $"{tenantId}-summary.txt"), summary.ToString(), cancellationToken);
    }

    /// <summary>
    /// Appends one row to <c>tokens.csv</c> in the output directory: when, which tenant and email,
    /// which deployment and knobs, tokens in and out, and whether the model's body was used or the
    /// fallback. Rows accumulate across runs, so comparing two models is a spreadsheet away.
    /// </summary>
    private async Task AppendCostRowAsync(
        string dir, int tenantId, int userId, MonthlyDigestSlot slot, ComposeDigestOutput output, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dir, "tokens.csv");
        if (!File.Exists(path))
            await File.WriteAllTextAsync(path,
                "utc,tenant,user,slot,deployment,reasoning_effort,verbosity,source,input_tokens,output_tokens,total_tokens,reason\n",
                cancellationToken);

        var reason = (output.Reason ?? string.Empty).Replace('"', '\'').Replace('\n', ' ');
        var row = string.Join(',',
            DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture),
            tenantId,
            userId,
            slot,
            configuration["Llm:AzureOpenAi:Deployment"] ?? "",
            configuration["Llm:AzureOpenAi:ReasoningEffort"] ?? "",
            configuration["Llm:AzureOpenAi:Verbosity"] ?? "",
            output.Source,
            output.InputTokens,
            output.OutputTokens,
            output.InputTokens + output.OutputTokens,
            $"\"{reason}\"");

        await File.AppendAllTextAsync(path, row + "\n", cancellationToken);
    }

    /// <summary>
    /// Calls every slot proc for every scope group and writes each result - data or refusal - to
    /// <c>FreeDigest:Preview:CaptureDir</c>. No LLM call, nothing written to the task hub, the blob
    /// store or the database: this is the read half of a preview, saved for later.
    /// </summary>
    /// <summary>The name stored with the capture, or null for a capture taken before it was stored.</summary>
    private static string? CapturedTenantName(string replayDir, int tenantId)
    {
        foreach (var userId in CapturedFreeMonthlyDigestStore.CapturedUserIds(Path.GetFullPath(replayDir), tenantId))
            foreach (var slot in Enum.GetValues<MonthlyDigestSlot>())
            {
                var path = CapturedFreeMonthlyDigestStore.PathFor(Path.GetFullPath(replayDir), tenantId, userId, slot);
                if (!File.Exists(path))
                    continue;

                try
                {
                    var captured = JsonSerializer.Deserialize<CapturedSlot>(File.ReadAllText(path), CapturedFreeMonthlyDigestStore.Json);
                    if (captured?.TenantName is { Length: > 0 } name)
                        return name;
                }
                catch (JsonException)
                {
                    // A capture we cannot read is the replay's problem to report, not the masthead's.
                }
            }

        return null;
    }

    private async Task CaptureAsync(
        IServiceProvider sp, string captureDir, int tenantId, string tenantName, IReadOnlyList<DigestScopeGroup> groups,
        IReadOnlyList<MonthlyDigestSlot> slots, DateOnly monthStart, DateTime localNow,
        FreeMonthlySettings monthly, CancellationToken cancellationToken)
    {
        var repository = sp.GetRequiredService<IFreeMonthlyDigestRepository>();
        var captured = 0;
        var refused = 0;

        foreach (var group in groups)
        {
            foreach (var slot in slots)
            {
                var edition = EditionFor(slot, monthStart);
                var asOf = AsOfFor(edition, localNow);
                var userId = group.RepresentativeUserId;

                CapturedSlot record;
                try
                {
                    var data = await repository.GetSlotAsync(edition, tenantId, userId, asOf, monthly.AllowPersonNames, cancellationToken);
                    record = new CapturedSlot(tenantId, userId, slot, asOf, monthly.AllowPersonNames,
                        DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture), data, null, null, tenantName);
                    captured++;
                }
                catch (FreeMonthlyDigestRefusedException ex)
                {
                    // A refusal is part of the tenant's truth - replaying it must fail closed too.
                    record = new CapturedSlot(tenantId, userId, slot, asOf, monthly.AllowPersonNames,
                        DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture), null, ex.SqlErrorNumber, ex.Message, tenantName);
                    refused++;
                    logger.LogWarning("Capture: tenant {TenantId} user {UserId} {Slot} REFUSED by SQL error {Code} - captured as a refusal.",
                        tenantId, userId, slot, ex.SqlErrorNumber);
                }

                var path = CapturedFreeMonthlyDigestStore.Write(captureDir, record);
                logger.LogInformation("Capture: tenant {TenantId} user {UserId} {Slot} -> {Path}", tenantId, userId, slot, path);
            }
        }

        logger.LogInformation(
            "Capture complete: tenant {TenantId}, {Groups} scope group(s), {Captured} slot(s) captured, {Refused} refused, in {Dir}. "
            + "Replay with --FreeDigest:Preview:ReplayDir={Dir}",
            tenantId, groups.Count, captured, refused, captureDir, captureDir);
    }

    /// <summary>
    /// The pipeline's own node 1, unchanged: entitlement gate, recipients, unsubscribe filter,
    /// already-claimed filter, scope grouping. All reads. Returns the same scope groups production
    /// would compose for - so the preview writes one email per group, not one per tenant.
    /// </summary>
    private async Task<IReadOnlyList<DigestScopeGroup>> ResolveGroupsAsync(
        IServiceProvider sp, IFreeDigestRepository digestRepository, int tenantId, StringBuilder summary, CancellationToken cancellationToken)
    {
        /*  Replaying captures has to work with NO database at all - that is the point of capturing.
            Gate, recipients and scope grouping are all SQL, so in replay mode the groups come from
            the capture files themselves: one per representative user that was captured for this
            tenant. Real grouping already happened when the capture was taken.                    */
        if (configuration["FreeDigest:Preview:ReplayDir"] is { Length: > 0 } replayDir)
        {
            var users = CapturedFreeMonthlyDigestStore.CapturedUserIds(Path.GetFullPath(replayDir), tenantId);
            if (users.Count == 0)
                throw new InvalidOperationException(
                    $"No captures for tenant {tenantId} in {Path.GetFullPath(replayDir)}. Capture it first with --FreeDigest:Preview:CaptureDir.");

            summary.AppendLine($"REPLAY from {Path.GetFullPath(replayDir)}: {users.Count} captured scope group(s), no database used for slot data.");

            var forced = configuration.GetValue<int?>("FreeDigest:UserId");
            var chosen = forced is { } f
                ? users.Contains(f)
                    ? [f]
                    : throw new InvalidOperationException($"Tenant {tenantId} has no capture for user {f}. Captured: {string.Join(", ", users)}.")
                : string.Equals(configuration["FreeDigest:Preview:Groups"], "all", StringComparison.OrdinalIgnoreCase)
                    ? users
                    : users.Take(int.TryParse(configuration["FreeDigest:Preview:Groups"], out var n) && n > 0 ? n : 1).ToList();

            return chosen.Select(u => new DigestScopeGroup($"replay-u{u}", u, [])).ToList();
        }

        var gate = await digestRepository.EvaluateGateAsync(tenantId, cancellationToken);
        summary.AppendLine($"gate: {gate.Decision} - {gate.Reason} (recipients: {gate.RecipientCount})");

        var resolve = new ResolveDigestRecipientsActivity(
            digestRepository,
            sp.GetRequiredService<IInsightJsonRepository>(),
            sp.GetRequiredService<IScopeRepository>(),
            sp.GetRequiredService<FreeDigestMetrics>(),
            sp.GetRequiredService<ILogger<ResolveDigestRecipientsActivity>>());

        var resolved = await resolve.RunAsync(new ResolveDigestRecipientsInput(tenantId, null));
        var groupLimit = configuration["FreeDigest:Preview:Groups"];
        var forcedUser = configuration.GetValue<int?>("FreeDigest:UserId");
        summary.AppendLine($"resolve: proceed={resolved.ShouldProceed}, decision={resolved.Decision}, week ending {resolved.WeekEnding}, "
                           + $"{resolved.Groups.Count} scope group(s), {resolved.RecipientsWithoutScope} recipient(s) dropped with no scope");

        var claimed = await digestRepository.GetClaimedUserIdsAsync(tenantId, DateOnly.ParseExact(resolved.WeekEnding, "yyyy-MM-dd"), cancellationToken);
        summary.AppendLine($"already claimed for week ending {resolved.WeekEnding}: "
                           + (claimed.Count == 0 ? "none" : string.Join(", ", claimed)));

        if (resolved.ShouldProceed && resolved.Groups.Count > 0)
        {
            /*  COST CONTROL. A tenant can have many scope groups (1285 has 6), and every group is a
                full set of LLM calls. The preview defaults to the FIRST group only - enough to judge
                the writing - and takes the rest on request.                                       */
            if (forcedUser is { } user)
            {
                summary.AppendLine($"FreeDigest:UserId={user}: previewing that user only, not the {resolved.Groups.Count} resolved group(s).");
                return [resolved.Groups.FirstOrDefault(g => g.RepresentativeUserId == user)
                        ?? new DigestScopeGroup("forced-user", user, [])];
            }

            var take = string.Equals(groupLimit, "all", StringComparison.OrdinalIgnoreCase)
                ? resolved.Groups.Count
                : int.TryParse(groupLimit, out var n) && n > 0 ? n : 1;

            if (take < resolved.Groups.Count)
                summary.AppendLine($"previewing {take} of {resolved.Groups.Count} scope group(s) - pass --FreeDigest:Preview:Groups=all for every group.");

            return resolved.Groups.Take(take).ToList();
        }

        /*  The real pipeline stops here. The preview stops too unless asked not to - then it falls
            back to the first recipient WITH SCOPE, so a tenant whose recipients were all mailed
            earlier this week can still be previewed.                                            */
        if (!configuration.GetValue("FreeDigest:Preview:IgnoreClaims", false))
            throw new InvalidOperationException(
                $"The pipeline would send nothing for tenant {tenantId}: {resolved.Decision} - {resolved.Reason}. "
                + "Pass --FreeDigest:Preview:IgnoreClaims=true to preview anyway.");

        summary.AppendLine("IgnoreClaims: the pipeline would have exited here; previewing the first recipient with scope instead.");
        var userId = configuration.GetValue<int?>("FreeDigest:UserId")
                     ?? await FirstScopedRecipientAsync(sp, digestRepository, tenantId, cancellationToken);
        return [new DigestScopeGroup("preview", userId, [])];
    }

    /// <summary>
    /// Encrypt then decrypt the finished HTML in memory, the same codec the artifact store uses
    /// before it writes a blob - so a Key Vault or envelope problem shows up here, not on Sunday.
    /// Never throws: a preview must still produce its files if this environment has no key access.
    /// </summary>
    private static async Task<string> CodecRoundTripAsync(IServiceProvider sp, string html, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await sp.GetRequiredService<IReportEncryptor>().EncryptAsync(html, cancellationToken);
            var back = await sp.GetRequiredService<IReportDecryptor>().DecryptAsync(
                envelope.Content, envelope.EncryptedAesKey, envelope.KeyVaultObjectVersion, cancellationToken);

            return string.Equals(back, html, StringComparison.Ordinal)
                ? "encrypt/decrypt round trip ok (nothing stored)"
                : "ENCRYPT/DECRYPT MISMATCH - the decrypted HTML differs from the original";
        }
        catch (Exception ex)
        {
            return $"encrypt/decrypt not verified here: {ex.GetType().Name} - {ex.Message}";
        }
    }

    /// <summary>
    /// The slot's own Sunday in the previewed month. The Licence slot exists only in 5-Sunday months;
    /// in a 4-Sunday month it is previewed against the same month data (only the "Next week" line
    /// differs from what a real run would say).
    /// </summary>
    /// <summary>
    /// The instant each slot reports as at.
    ///
    /// <para>Normally each edition uses its OWN Sunday, because that is the day production would
    /// have generated it - so a preview of a whole month shows the position moving week by week.
    /// </para>
    ///
    /// <para><c>FreeDigest:Preview:AsOfToday=true</c> puts every slot on today instead. That is not
    /// how the digest runs, so it is never the default; it exists because a demo set usually wants
    /// five views of the CURRENT position rather than a re-enactment of the month, and the first
    /// Sunday of a month is always the thinnest week there is.</para>
    /// </summary>
    private DateTime AsOfFor(MonthlyDigestEdition edition, DateTime localNow) =>
        configuration.GetValue("FreeDigest:Preview:AsOfToday", false)
            ? MonthlyDigestCalendar.AsOfWithinMonth(localNow, edition with { Sunday = edition.CurrMonthEnd })
            : MonthlyDigestCalendar.AsOfWithinMonth(localNow, edition);

    private static MonthlyDigestEdition EditionFor(MonthlyDigestSlot slot, DateOnly monthStart)
    {
        var firstSunday = monthStart.AddDays(((int)DayOfWeek.Sunday - (int)monthStart.DayOfWeek + 7) % 7);
        var sunday = firstSunday.AddDays(7 * (int)slot);
        if (sunday.Month == monthStart.Month)
            return MonthlyDigestCalendar.For(sunday);

        var last = MonthlyDigestCalendar.For(sunday.AddDays(-7));
        return last with { Slot = slot };
    }

    private async Task<int> FirstScopedRecipientAsync(IServiceProvider sp, IFreeDigestRepository repository, int tenantId, CancellationToken cancellationToken)
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

    private static string Debug(
        MonthlyComposeDiagnostics result, MonthlyDigestEdition edition, int tenantId, int userId, string subject, string codec)
    {
        var d = result.Data;
        var sb = new StringBuilder();
        sb.AppendLine($"Tenant {tenantId}, user {userId}, slot {edition.Slot}, {edition.PeriodLabel}");
        sb.AppendLine($"SUBJECT: {subject}");
        sb.AppendLine($"AsOf {d.AsOf:yyyy-MM-dd HH:mm}, previous month {edition.PrevMonthStart:MMMM yyyy}");
        sb.AppendLine($"SOURCE: {result.Output.Source}   tokens {result.Output.InputTokens} in / {result.Output.OutputTokens} out");
        if (result.Output.Reason is { } reason)
            sb.AppendLine($"WHY NOT THE LLM BODY: {reason}");
        sb.AppendLine($"HeadlineSource: {d.HeadlineSource}");
        sb.AppendLine($"Artifact codec: {codec}");

        sb.AppendLine().AppendLine("== FACTS (FactKey = value | section | window | impact | tier | as-at | headline) ==");
        foreach (var f in d.Facts)
            sb.AppendLine($"{f.FactKey,-36} = {f.FactValue,7} | {f.Section,-13} | {f.WindowScope,-5} | {f.ImpactClass,-22} | {f.SeverityTier} | {(f.AsAtRequired ? "as-at" : "     ")} | {(f.IsHeadline ? "HEADLINE" : "")}   {f.DisplayLabel}");

        sb.AppendLine().AppendLine("== DETECTOR POLICY ==");
        foreach (var p in d.DetectorPolicy)
            sb.AppendLine($"{p.Detector,-32} eligible {p.Eligible,5}  flagged {p.Flagged,5}  {p.FlaggedPct,6:0.0}%  {p.EmitMode,-10} {p.Note}");

        sb.AppendLine().AppendLine("== CANDIDATES (DefaultSlot 1/2 are the ones the email names) ==");
        foreach (var c in d.Candidates)
            sb.AppendLine($"slot {c.DefaultSlot?.ToString() ?? "-"} | {c.Detector,-32} #{c.RankInDetector} | {c.EntityKind} '{c.EntityLabel}'{(c.ContextLabel is null ? "" : $" at '{c.ContextLabel}'")} | item {c.ItemCount} of {c.BaseCount} | pct {c.MetricPct} vs {c.TenantPct} | {c.ProblemCount} of {c.PopulationCount} {(c.EventDate is { } e ? $"| {e:yyyy-MM-dd}" : "")}");

        sb.AppendLine().AppendLine("== DATA QUALITY ==");
        foreach (var q in d.DataQuality)
            sb.AppendLine($"{q.Code,-34} {q.ItemCount,7}  {q.Detail}");

        sb.AppendLine().AppendLine("== MODEL INPUT (user message) ==").AppendLine(result.UserMessage);
        sb.AppendLine().AppendLine("== MODEL RAW DRAFT (before names were bound) ==").AppendLine(result.RawDraft);
        sb.AppendLine().AppendLine("== FINAL BODY ==").AppendLine(result.Output.Body);
        return sb.ToString();
    }
}
