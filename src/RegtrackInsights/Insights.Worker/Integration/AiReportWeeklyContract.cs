using System.Text.Json.Serialization;

namespace Insights.Worker.Integration;

/// <summary>
/// ADR-0003 (2026-09-14): the wire contract for POST /v2/api/ai-report/weekly/upsert, exactly as
/// specified in ai-report-integration.md. This is an EXTERNAL contract this worker does not own -
/// field names, casing and the three distinct response envelope shapes below are copied verbatim
/// from that document, not invented here. Nothing in this file is domain logic: it exists only so
/// PostInsightJsonActivity has something to serialize onto (and deserialize off) the wire.
/// </summary>
public sealed record AiReportWeeklyUpsertRequest(
    [property: JsonPropertyName("customer_id")] int CustomerId,
    [property: JsonPropertyName("user_id")] long UserId,
    [property: JsonPropertyName("period_start_date")] DateOnly PeriodStartDate,
    [property: JsonPropertyName("report")] AiReportWeeklyReport Report,
    [property: JsonPropertyName("model_version")] string? ModelVersion,
    [property: JsonPropertyName("source_reference")] string? SourceReference);

/// <summary>
/// The free-form "report" object the spec allows any shape for. ADR-0003 D4: narrative +
/// provenance + the one data-layer-verified focus figure - deliberately NOT the full internal
/// aggregate set (that stays a paid-tier boundary, not a wire-format concern).
/// </summary>
public sealed record AiReportWeeklyReport(
    [property: JsonPropertyName("headline")] string Headline,
    [property: JsonPropertyName("explanation")] string Explanation,
    [property: JsonPropertyName("severity_band")] string SeverityBand,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("focus")] AiReportWeeklyFocus Focus);

public sealed record AiReportWeeklyFocus(
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("value")] int Value,
    [property: JsonPropertyName("denominator")] int? Denominator,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("display_text")] string DisplayText);

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
