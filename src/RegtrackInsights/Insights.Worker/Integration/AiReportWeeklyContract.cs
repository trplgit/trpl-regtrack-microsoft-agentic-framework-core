using System.Text.Json.Serialization;

namespace Insights.Worker.Integration;

/// <summary>
/// ADR-0003 (2026-09-14): the wire contract for POST /v2/api/ai-report/weekly/upsert, exactly as
/// specified in ai-report-integration.md. This is an EXTERNAL contract this worker does not own -
/// field names, casing and the three distinct response envelope shapes below are copied verbatim
/// from that document, not invented here. Nothing in this file is domain logic: it exists only so
/// PostInsightJsonActivity has something to serialize onto (and deserialize off) the wire.
///
/// <para>ADR-0004 (revised 2026-09-24): <see cref="Report"/> IS the insight card, in the exact
/// field list the /insights hub binds. The receiver stores it free-form and renders it as the
/// current week's hero card and, once later weeks exist, as a compact previous-week card from the
/// same object. Previous weeks are the receiver's own stored periods, never resent.</para>
/// </summary>
public sealed record AiReportWeeklyUpsertRequest(
    [property: JsonPropertyName("customer_id")] int CustomerId,
    [property: JsonPropertyName("user_id")] long UserId,
    [property: JsonPropertyName("period_start_date")] DateOnly PeriodStartDate,
    [property: JsonPropertyName("report")] AiReportWeeklyInsight Report,
    [property: JsonPropertyName("model_version")] string? ModelVersion,
    [property: JsonPropertyName("source_reference")] string? SourceReference);

// -- the insight card (the report object) ---------------------------------------------------

/// <summary>
/// The ten fields the frontend binds. Adding one here without the frontend changing with it is a
/// silent contract break, so this record stays exactly as wide as that page is.
/// </summary>
public sealed record AiReportWeeklyInsight(
    [property: JsonPropertyName("insight_id")] string InsightId,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("week_of")] string WeekOf,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("headline")] string Headline,
    [property: JsonPropertyName("narrative")] string Narrative,
    [property: JsonPropertyName("primary_metric")] AiReportWeeklyPrimaryMetric PrimaryMetric,
    [property: JsonPropertyName("supporting_metrics")] IReadOnlyList<AiReportWeeklySupportingMetric> SupportingMetrics);

public sealed record AiReportWeeklyPrimaryMetric(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("current")] int Current,
    [property: JsonPropertyName("target")] int Target,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("direction")] string Direction);

public sealed record AiReportWeeklySupportingMetric(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] int Value,
    [property: JsonPropertyName("unit")] string Unit);

// -- responses (unchanged) ------------------------------------------------------------------

/// <summary>The 200 OK body. `result.status` is "Created" on first POST for a key, "Updated" on every re-POST.</summary>
public sealed record AiReportWeeklyUpsertResult(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("aiWeeklyReportID")] long? AiWeeklyReportId,
    [property: JsonPropertyName("revisionCount")] int? RevisionCount,
    [property: JsonPropertyName("createdOnUtc")] DateTimeOffset? CreatedOnUtc,
    [property: JsonPropertyName("updatedOnUtc")] DateTimeOffset? UpdatedOnUtc);

/// <summary>The 200 OK envelope. `error` is a STRING here ("" on success) - contrast <see cref="AiReportWeeklyErrorEnvelope"/> where the same property name is an object.</summary>
public sealed record AiReportWeeklySuccessEnvelope(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("result")] AiReportWeeklyUpsertResult? Result);

/// <summary>The 404 envelope (e.g. CUSTOMER_USER_NOT_FOUND). `error` is an OBJECT here - same property name, different JSON type than the 200 envelope, per the spec.</summary>
public sealed record AiReportWeeklyErrorEnvelope(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")] AiReportWeeklyErrorDetail? Error,
    [property: JsonPropertyName("result")] AiReportWeeklyUpsertResult? Result);

public sealed record AiReportWeeklyErrorDetail(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>The 400 body. FLAT - per the spec, NOT wrapped in success/error/result like every other response.</summary>
public sealed record AiReportWeeklyValidationErrorEnvelope(
    [property: JsonPropertyName("errors")] List<AiReportWeeklyValidationError>? Errors);

public sealed record AiReportWeeklyValidationError(
    [property: JsonPropertyName("field")] string? Field,
    [property: JsonPropertyName("message")] string? Message);
