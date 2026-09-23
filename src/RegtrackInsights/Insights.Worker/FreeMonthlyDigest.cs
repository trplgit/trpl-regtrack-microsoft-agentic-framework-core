using System.Globalization;
using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// The free digest email content (spec Sec.10).
///
/// <para><b>Only the token caps are configuration</b> - Budget:FreeMonthlyTokenCap:* - because they
/// are a spend control an environment may legitimately set differently. They have no default in
/// code, so a missing one stops the worker at startup naming the key (the same "fail at startup,
/// not at 3am" stance as FreeDigestRegistration's Require()).</para>
///
/// <para>The rest are product decisions with one right answer everywhere, so they live here.</para>
/// </summary>
public sealed class FreeMonthlySettings
{
    /// <summary>
    /// Passed to sql/38's <c>@AllowPersonNames</c>. true names people in the Users email; false
    /// describes them without a name ("one person who is no longer an active user ...").
    ///
    /// <para>[MOVED OUT OF CONFIG 2026-09-22] This and <see cref="MaxDraftAttempts"/> were required
    /// appsettings keys, which meant every environment had to carry them and a missing one stopped
    /// the worker at startup. Neither is environment-specific: naming people is a product decision
    /// that is the same everywhere, and the attempt count is a behaviour of the composer. A value
    /// that is identical in every environment does not belong in per-environment config.</para>
    /// </summary>
    public bool AllowPersonNames { get; init; } = true;

    /// <summary>Budget:FreeMonthlyTokenCap:{Overview|Users|Location|Act|Licence} - total (prompt + completion) per call.</summary>
    public required IReadOnlyDictionary<MonthlyDigestSlot, int> TokenCaps { get; init; }

    /// <summary>
    /// How many times the model may write one email. 1 means a rejected draft falls straight back
    /// to the deterministic body; above 1, a rejected draft is handed its own failure list and
    /// rewritten, and each attempt is a billed call.
    ///
    /// <para>1 is deliberate: [MEASURED 2026-09-21] the repair pass now fixes the style failures
    /// that used to cause rejections, so a second attempt would double the spend to rescue a draft
    /// that is already being rescued deterministically.</para>
    /// </summary>
    public int MaxDraftAttempts { get; init; } = 1;

    public int TokenCapFor(MonthlyDigestSlot slot) => TokenCaps[slot];

    /// <summary>Binds and validates. Throws on a missing or bad value - including a prompt file that does not exist.</summary>
    public static FreeMonthlySettings Build(IConfiguration configuration, string promptDirectory)
    {
        var slots = Enum.GetValues<MonthlyDigestSlot>();

        var caps = slots.ToDictionary(s => s, s => RequiredPositiveInt(configuration, $"Budget:FreeMonthlyTokenCap:{s}"));

        var settings = new FreeMonthlySettings
        {
            TokenCaps = caps,
        };

        /*  A missing prompt file would otherwise surface on the first Sunday, per scope group, as a
            FileNotFoundException inside an activity. Every digest uses these prompts, so a worker
            that cannot find them refuses to start.                                              */
        var root = Path.IsPathRooted(promptDirectory) ? promptDirectory : Path.Combine(AppContext.BaseDirectory, promptDirectory);
        var files = new[] { FreeMonthlyPromptFiles.SharedRules() }
            .Concat(slots.Select(FreeMonthlyPromptFiles.ForSlot));
        foreach (var file in files)
            if (!File.Exists(Path.Combine(root, file)))
                throw new InvalidOperationException($"Free digest prompt '{file}' does not exist in {root} - it ships as Content from src/RegtrackInsights/prompts.");

        return settings;
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{key} is not configured. It has no default in code - add it to appsettings.json.");

    private static int RequiredPositiveInt(IConfiguration configuration, string key) =>
        int.TryParse(Required(configuration, key), out var value) && value > 0
            ? value
            : throw new InvalidOperationException($"{key} must be a positive whole number.");
}

/// <summary>
/// Composes one monthly digest body for a scope group: slot proc -> prompt -> LLM -> validate -> bind,
/// with the deterministic <see cref="FreeMonthlyFallbackBody"/> on any LLM-side failure.
///
/// A SQL refusal (<see cref="FreeMonthlyDigestRefusedException"/>) is NOT caught here - it is a
/// different failure class from an LLM rejection. The fallback exists so an email always goes out
/// when the DATA is good; when SQL refuses the data, nothing may go out (CLAUDE.md non-negotiable 2).
/// </summary>
public sealed class FreeMonthlyDigestComposer(
    IFreeMonthlyDigestRepository repository,
    FreeMonthlyDigestWriter writer,
    FreeMonthlySettings monthlySettings,
    FreeDigestSettings digestSettings,
    ILogger<FreeMonthlyDigestComposer> logger)
{
    public async Task<ComposeDigestOutput> ComposeAsync(
        int tenantId, int representativeUserId, MonthlyDigestEdition edition, string? asOfOverride, CancellationToken cancellationToken = default) =>
        (await ComposeWithDiagnosticsAsync(tenantId, representativeUserId, edition, asOfOverride, cancellationToken)).Output;

    /// <summary>
    /// Same as <see cref="ComposeAsync"/>, plus what the model was given and what it wrote - for
    /// FreeMonthlyPreviewWorker, where tuning a prompt means reading exactly that.
    /// </summary>
    public async Task<MonthlyComposeDiagnostics> ComposeWithDiagnosticsAsync(
        int tenantId, int representativeUserId, MonthlyDigestEdition edition, string? asOfOverride, CancellationToken cancellationToken = default)
    {
        /*  ONE clock read, clamped into the edition's month. CurrMonthStart comes from the Sunday
            (the claim key), @AsOf from now - sql/34 refuses 51237 if the two disagree, so the clamp
            is what keeps a late retry across a month boundary from being refused.               */
        var localNow = string.IsNullOrWhiteSpace(asOfOverride)
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, digestSettings.ScheduleTimeZone)
            : DateTime.Parse(asOfOverride, CultureInfo.InvariantCulture);
        var asOf = MonthlyDigestCalendar.AsOfWithinMonth(localNow, edition);

        var data = await repository.GetSlotAsync(edition, tenantId, representativeUserId, asOf, monthlySettings.AllowPersonNames, cancellationToken);

        foreach (var dq in data.DataQuality.Where(d => d.ItemCount > 0))
            logger.LogInformation(
                "FreeMonthlyDigestComposer: tenant {TenantId} user {UserId} {Slot} - data_quality {Code} = {Count}.",
                tenantId, representativeUserId, edition.Slot, dq.Code, dq.ItemCount);

        var prompt = FreeMonthlyDigestPrompt.Build(data);
        var systemPrompt = await writer.LoadSystemPromptAsync(edition.Slot, cancellationToken);

        /*  [ADDED 2026-09-20] The model gets MaxDraftAttempts tries, each rejection handed back as
            the validator's own failure list. Without this, one banned phrase costs the reader the
            whole written email and substitutes the deterministic fallback - which made every rule
            expensive and pushed us to loosen rules that should have stayed. The validator is still
            the only gate and still deterministic: a redraft does not lower the bar, it just asks
            again.                                                                                */
        var userMessage = prompt.UserMessage;
        FreeDigestEmail draft;
        string reason;
        var inputTokens = 0;
        var outputTokens = 0;
        var attempt = 0;

        while (true)
        {
            attempt++;
            draft = await writer.WriteAsync(systemPrompt, userMessage, monthlySettings.TokenCapFor(edition.Slot), cancellationToken);
            inputTokens += draft.InputTokens;
            outputTokens += draft.OutputTokens;

            if (draft.Source != FreeDigestSource.Llm)
            {
                reason = draft.SkippedReason ?? "LLM skipped";
                logger.LogWarning(
                    "FreeMonthlyDigestComposer: tenant {TenantId} user {UserId} {Slot} - LLM SKIPPED, falling back. Tokens spent anyway: {InputTokens} in / {OutputTokens} out. {Reason}",
                    tenantId, representativeUserId, edition.Slot, inputTokens, outputTokens, reason);
                break;
            }

            /*  Presentation is fixed, never rejected: normalise emphasis, then delete the sentences
                that comment on the figures instead of stating them. Only then is what remains
                checked for truth. Deleting can never add a claim, so the order is safe.        */
            var normalized = FreeMonthlyDraftNormalizer.Normalize(draft.Body);
            var repaired = FreeMonthlyDraftRepair.Apply(normalized, prompt);

            if (repaired.Removed.Count > 0)
                logger.LogInformation(
                    "FreeMonthlyDigestComposer: tenant {TenantId} user {UserId} {Slot} - removed {Count} sentence(s) before validating: {Removed}",
                    tenantId, representativeUserId, edition.Slot, repaired.Removed.Count, string.Join(" | ", repaired.Removed));

            var validation = FreeMonthlyDigestValidator.Validate(repaired.Body, prompt);

            if (validation.Advisories.Count > 0)
                logger.LogInformation(
                    "FreeMonthlyDigestComposer: tenant {TenantId} user {UserId} {Slot} - advisories (email still sent): {Advisories}",
                    tenantId, representativeUserId, edition.Slot, string.Join("; ", validation.Advisories));


            if (validation.IsValid)
            {
                // The repaired text is what was validated, so it is what the reader gets.
                var body = FreeMonthlyPlaceholderBinder.Bind(repaired.Body, prompt.Bindings) + "\n\n" + FreeMonthlyClosing.For(edition);

                logger.LogInformation(
                    "FreeMonthlyDigestComposer: tenant {TenantId} user {UserId} {Slot} - LLM body accepted on attempt {Attempt}. Tokens: {InputTokens} in / {OutputTokens} out.",
                    tenantId, representativeUserId, edition.Slot, attempt, inputTokens, outputTokens);
                return new MonthlyComposeDiagnostics(
                    new ComposeDigestOutput(body, "Llm", null, inputTokens, outputTokens), prompt.Data, prompt.UserMessage, draft.Body);
            }

            reason = "validator rejected the LLM body: " + string.Join("; ", validation.FailedChecks);

            if (attempt >= monthlySettings.MaxDraftAttempts)
            {
                logger.LogWarning(
                    "FreeMonthlyDigestComposer: tenant {TenantId} user {UserId} {Slot} - LLM body REJECTED on attempt {Attempt} of {MaxAttempts}, falling back. Tokens spent anyway: {InputTokens} in / {OutputTokens} out. {Reason}\nRejected body was:\n{Body}",
                    tenantId, representativeUserId, edition.Slot, attempt, monthlySettings.MaxDraftAttempts, inputTokens, outputTokens, reason, draft.Body);
                break;
            }

            logger.LogInformation(
                "FreeMonthlyDigestComposer: tenant {TenantId} user {UserId} {Slot} - attempt {Attempt} of {MaxAttempts} rejected, redrafting. {Reason}",
                tenantId, representativeUserId, edition.Slot, attempt, monthlySettings.MaxDraftAttempts, reason);

            userMessage = FreeMonthlyRedraft.Message(prompt.UserMessage, draft.Body, validation.FailedChecks);
        }

        return new MonthlyComposeDiagnostics(
            new ComposeDigestOutput(FreeMonthlyFallbackBody.Build(prompt.Data), "Fallback", reason, inputTokens, outputTokens),
            prompt.Data, prompt.UserMessage, draft.Body);
    }
}

/// <summary>A composed monthly body plus its inputs and the model's raw draft (accepted or rejected).</summary>
public sealed record MonthlyComposeDiagnostics(ComposeDigestOutput Output, MonthlyDigestData Data, string UserMessage, string RawDraft);
