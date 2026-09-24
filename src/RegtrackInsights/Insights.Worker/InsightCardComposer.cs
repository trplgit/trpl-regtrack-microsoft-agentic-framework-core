using System.Globalization;
using Insights.Agents;
using Insights.Data;
using Insights.Domain;
using Microsoft.Extensions.Logging;

namespace Insights.Worker;

/// <summary>
/// One composed insight card, plus how it was produced. <see cref="Source"/> is <c>llm</c> or
/// <c>fallback</c> - the card itself deliberately carries no provenance, so this is the only place
/// a degrading card text can be noticed.
/// </summary>
public sealed record InsightCardResult(
    InsightCard Card, MonthlyDigestSlot Subject, int InputTokens, int OutputTokens,
    string Source, string? Reason, string UserMessage, string RawDraft);

/// <summary>
/// Composes the weekly insight card for one scope group (ADR-0004, revised 2026-09-24).
///
/// <para>The subject is the one the EMAIL covers that Sunday - <see cref="MonthlyDigestCalendar"/>
/// decides it from the date alone, so week 2 is People for both lanes and a reader never gets a
/// card about one subject and an email about another. One procedure read per scope group.</para>
///
/// <para>The lanes stay independent in everything else: this has its own prompt
/// (07b_insight_card.md), its own model input, and its own validator pass, so tuning the email's
/// wording cannot change the card.</para>
///
/// <para>A SQL refusal (<see cref="FreeMonthlyDigestRefusedException"/>) is not caught: when the
/// data layer refuses, nothing is posted (CLAUDE.md non-negotiable 2). An LLM-side failure falls
/// back to deterministic text built from the same procedure values, and the card still ships.</para>
/// </summary>
public sealed class InsightCardComposer(
    IFreeMonthlyDigestRepository repository,
    InsightCardWriter writer,
    FreeMonthlySettings monthlySettings,
    FreeDigestSettings digestSettings,
    ILogger<InsightCardComposer> logger)
{
    public async Task<InsightCardResult> ComposeAsync(int tenantId, int representativeUserId, DateOnly weekEnding, string? asOfOverride, CancellationToken cancellationToken = default)
    {
        /*  Refuse here rather than letting MonthlyDigestCalendar.For throw an ArgumentException
            inside an activity retry loop, where it would be retried three times and logged as an
            infrastructure fault instead of the input error it is.                               */
        if (weekEnding.DayOfWeek != DayOfWeek.Sunday)
            throw new InvalidOperationException(
                $"WeekEnding {weekEnding:yyyy-MM-dd} is a {weekEnding.DayOfWeek}, not a Sunday - the weekly insight is keyed on the Sunday that closes its week.");

        var edition = MonthlyDigestCalendar.For(weekEnding);

        // Same clock discipline as the email composer: one read, clamped into the edition's month (sql/34 51237).
        var localNow = string.IsNullOrWhiteSpace(asOfOverride)
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, digestSettings.ScheduleTimeZone)
            : DateTime.Parse(asOfOverride, CultureInfo.InvariantCulture);
        var asOf = MonthlyDigestCalendar.AsOfWithinMonth(localNow, edition);

        var data = await repository.GetSlotAsync(edition, tenantId, representativeUserId, asOf, monthlySettings.AllowPersonNames, cancellationToken);
        return await ComposeFromDataAsync(data, tenantId, representativeUserId, weekEnding, cancellationToken);
    }

    /// <summary>Composes from subject data already in hand - the replay preview, and tests.</summary>
    public async Task<InsightCardResult> ComposeFromDataAsync(
        MonthlyDigestData data, int tenantId, long userId, DateOnly weekEnding, CancellationToken cancellationToken = default)
    {
        var input = InsightCardInput.Build(data);
        var text = await writer.WriteAsync(input, digestSettings.InsightJsonTokenCap, cancellationToken);

        if (text.Source != "llm")
            logger.LogWarning(
                "InsightCardComposer: tenant {TenantId} user {UserId} {Subject} - card text fell back to the deterministic build. Tokens spent anyway: {In} in / {Out} out. {Reason}",
                tenantId, userId, input.Subject, text.InputTokens, text.OutputTokens, text.Reason);
        else
            logger.LogInformation(
                "InsightCardComposer: tenant {TenantId} user {UserId} {Subject} - card text accepted. Tokens: {In} in / {Out} out.",
                tenantId, userId, input.Subject, text.InputTokens, text.OutputTokens);

        var card = InsightCardBuilder.Build(input, tenantId, userId, weekEnding, text);
        return new InsightCardResult(card, input.Subject, text.InputTokens, text.OutputTokens, text.Source, text.Reason, input.UserMessage, text.RawDraft);
    }
}
