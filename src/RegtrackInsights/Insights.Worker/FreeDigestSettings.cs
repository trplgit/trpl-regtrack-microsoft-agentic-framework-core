using Insights.Domain;

namespace Insights.Worker;

/// <summary>
/// Everything the digest needs from configuration, resolved once at startup instead of being
/// passed as literals at each call site. Bound from Budget:*, Email:* and Agents:* - see
/// FreeDigestRegistration.
/// </summary>
public sealed class FreeDigestSettings
{
    /// <summary>
    /// Budget:InsightJsonTokenCap - the insight JSON lane's own cap. (The email's caps are
    /// Budget:FreeMonthlyTokenCap:*, in FreeMonthlySettings.)
    ///
    /// [BUG FOUND LIVE, 2026-09-13] ComposeInsightJsonActivity originally shared TokenCap with the
    /// free-digest email lane. InsightNarrativeWriter.CompletionTokenBudget estimates the prompt's
    /// own cost and clamps the completion budget to whatever is left under the cap (floor 60
    /// tokens) - so when 07_insight_json_narrative.md grew past ~7.8KB (~2100 estimated prompt
    /// tokens alone), NOTHING was left of the shared 2100 cap, and every insight-JSON call was
    /// silently clamped to the 60-token floor regardless of what the prompt's own word-count
    /// guidance asked for. The headline/explanation length looked "stuck short" no matter how the
    /// prompt was edited, because the real ceiling was the shared budget, not the prompt text.
    /// A separate cap means growing either prompt file only ever affects its own lane's budget.
    /// </summary>
    public int InsightJsonTokenCap { get; init; } = 3000;

    /// <summary>Email:FromAddress. Must be a sender verified with the provider, or every send is rejected.</summary>
    public required string FromAddress { get; init; }

    /// <summary>Email:FromName.</summary>
    public required string FromName { get; init; }

    /// <summary>Base URL for email-hosted images. The digest template owns the asset paths.</summary>
    public string CdnBaseUrl { get; init; } = string.Empty;

    /// <summary>Email:UpgradeUrl - the conversion link. The gap between a number and its explanation is the pitch.</summary>
    public required string UpgradeUrl { get; init; }

    /// <summary>Email:PortalUrl - the plain "go to your RegTrack account" link in the footer, distinct from UpgradeUrl (that one sells the paid tier; this one just gets an existing user back into the product they already have). Optional: an empty footer link is a cosmetic gap, not a reason to fail startup.</summary>
    public string? PortalUrl { get; init; }

    /// <summary>Email:UnsubscribeBaseUrl.</summary>
    public required string UnsubscribeBaseUrl { get; init; }

    /// <summary>
    /// Email:UnsubscribeSigningKey. Signs the unsubscribe link so it cannot be enumerated.
    /// The RegTrack API endpoint needs the SAME value to validate what it receives.
    /// </summary>
    public required string UnsubscribeSigningKey { get; init; }

    /// <summary>
    /// NON-PRODUCTION SAFETY VALVE. When set, every digest is delivered to this address instead
    /// of the recipient the database resolved.
    ///
    /// UAT is typically a copy of production, so its User table holds REAL customer addresses.
    /// Without this, a single RunWeeklyAsync against UAT emails real people a test digest
    /// carrying placeholder upgrade links. Recipients are still resolved, scoped and rendered
    /// exactly as in production - only the destination changes, so the run still proves the
    /// per-recipient path.
    ///
    /// MUST be empty in production. FreeDigestService logs a warning on every run while it is set.
    /// </summary>
    public string? RecipientOverride { get; init; }

    /// <summary>FreeDigest:Schedule:Enabled. Off by default - a worker must not mail customers merely because it booted.</summary>
    public bool ScheduleEnabled { get; init; }

    /// <summary>
    /// FreeDigest:Schedule:CheckIntervalMinutes. The LONGEST the weekly lane ever sleeps before
    /// re-reading the clock - it also wakes exactly at each phase's start (see
    /// FreeDigestScheduleClock), so this no longer delays a phase. On Generate/Send day it is the
    /// catch-up re-check interval. Ticking often is safe - the per-recipient claim is the guarantee.
    /// </summary>
    public TimeSpan ScheduleCheckInterval { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>Pause between tenants. The free lane is lowest priority (10.3) and must not starve a paying user's on-demand run.</summary>
    public TimeSpan SchedulePerTenantDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// ADR-0001 (2026-09-10) - the two-phase free digest. FreeDigest:Schedule:TimeZone, a Windows/
    /// ICU timezone id (default "India Standard Time" - confirmed IST per the product decision).
    /// </summary>
    public TimeZoneInfo ScheduleTimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>FreeDigest:Schedule:GenerateDay. The day the two-phase system generates artifacts. Default Sunday.</summary>
    public DayOfWeek GenerateDay { get; init; } = DayOfWeek.Sunday;

    /// <summary>FreeDigest:Schedule:SendDay. The day the two-phase system actually mails recipients. Default Monday.</summary>
    public DayOfWeek SendDay { get; init; } = DayOfWeek.Monday;

    /// <summary>FreeDigest:Schedule:GenerateHourLocal. Hour, in ScheduleTimeZone, generation opens on GenerateDay. Default 0 (midnight).</summary>
    public int GenerateHourLocal { get; init; }

    /// <summary>
    /// FreeDigest:Schedule:SendHourLocal. Hour, in ScheduleTimeZone, the send window opens. Default
    /// 8 (8am): ~5,000 recipients at 5/sec finish by ~08:25, so mail is in inboxes before 9am.
    /// </summary>
    public int SendHourLocal { get; init; } = 8;

    /// <summary>
    /// FreeDigest:Artifact:FreshnessDays. An artifact generated more than this many days ago is
    /// never dispatched, even if found - a stale digest is a wrong digest (sql/29). Default 3:
    /// generous enough to survive a Sunday generation run that finishes late, tight enough that a
    /// forgotten artifact from three weeks ago can never suddenly get mailed.
    /// </summary>
    public int ArtifactFreshnessDays { get; init; } = 3;

    /// <summary>FreeDigest:Artifact:RetentionDays. How long a dispatched (or never-dispatched) artifact's blob + index row survive before the purge sweep deletes them. ADR-0001 D7 - a placeholder pending a DPO-confirmed retention period.</summary>
    public int ArtifactRetentionDays { get; init; } = 90;

    /// <summary>
    /// FreeDigest:InsightApi:Enabled (ADR-0002, 2026-09-11) - the weekly per-user "current
    /// insight" JSON lane. Off by default: the destination endpoint is not configured yet, and
    /// this lane must not start POSTing real customer data to a placeholder URL. Read as an
    /// optional value, NOT via Require() - the worker must be able to boot before the endpoint
    /// exists (see FreeDigestRegistration.BuildSettings).
    /// </summary>
    public bool InsightApiEnabled { get; init; }

    /// <summary>
    /// FreeDigest:InsightApi:BaseUrl - the environment's ai-report-integration.md base URL (e.g.
    /// "https://uat.example.com"), NOT a complete endpoint URL. PostInsightJsonActivity appends
    /// the "/v2/api/ai-report/weekly/upsert" path itself (ADR-0003 D7). Only meaningful when
    /// <see cref="InsightApiEnabled"/> is true - required (and validated as an absolute URL) at
    /// startup when it is, see FreeDigestRegistration.BuildSettings.
    /// </summary>
    public string? InsightApiUrl { get; init; }

    /// <summary>FreeDigest:InsightApi:ApiKey - sent as a Bearer token. From Key Vault in production, never a literal in appsettings.</summary>
    public string? InsightApiKey { get; init; }

    /// <summary>FreeDigest:InsightApi:TimeoutSeconds - a wedged endpoint must surface as a failed POST, not hang an activity forever.</summary>
    public TimeSpan InsightApiTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// TESTING ONLY. FreeDigest:DebugDumpHtmlDir - when set, PersistDigestArtifactActivity writes
    /// a plain (unencrypted) copy of the exact HTML it is about to store to this local directory,
    /// alongside the normal encrypted blob write. Lets a manual GENERATE run be inspected by
    /// opening the file directly, without needing to decrypt the blob or wait for SEND.
    ///
    /// Leave unset in production - this is purely a local convenience, not a delivery path, and it
    /// writes real (if UAT) tenant content to a local disk.
    /// </summary>
    public string? DebugDumpHtmlDir { get; init; }

    /// <summary>The address a digest should actually be delivered to, honouring <see cref="RecipientOverride"/>.</summary>
    public string ResolveDeliveryAddress(string resolvedRecipientEmail) =>
        string.IsNullOrWhiteSpace(RecipientOverride) ? resolvedRecipientEmail : RecipientOverride;

    /// <summary>
    /// Per-recipient unsubscribe link.
    ///
    /// TODO (spec Section 5.4): this is currently an addressing scheme only - there is no durable
    /// opt-out store behind it yet, and sql/06 still has the TODO where opt-outs should be
    /// subtracted from the recipient count. Opt-out MUST survive tier changes, or an
    /// upgrade/downgrade cycle silently re-subscribes someone who asked to stop. Replace the
    /// query string with a signed, non-guessable token when that store lands.
    /// </summary>
    public string BuildUnsubscribeUrl(int customerId, long userId)
    {
        if (string.IsNullOrWhiteSpace(UnsubscribeBaseUrl))
            return string.Empty;

        var separator = UnsubscribeBaseUrl.Contains('?') ? "&" : "?";
        var token = UnsubscribeToken.Create(UnsubscribeSigningKey, customerId, userId);

        return $"{UnsubscribeBaseUrl}{separator}c={customerId}&u={userId}&t={token}";
    }
}

