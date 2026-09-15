using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using DurableTask.Core;
using Insights.Data;
using Insights.Domain;
using Insights.Worker.Integration;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record PostInsightJsonInput(int CustomerId, long UserId, string WeekEnding, FreeDigestAggregates Aggregates, InsightNarrative Narrative);

public sealed record PostInsightJsonOutput(bool Posted, string? Reason);

/// <summary>
/// ADR-0002 (2026-09-11) node 3 / ADR-0003 (2026-09-14): deliver one recipient's insight JSON to
/// the external ai-report-integration.md API (FreeDigest:InsightApi:BaseUrl +
/// /v2/api/ai-report/weekly/upsert). No LLM call, no re-compose - only the wire translation
/// (Integration/AiReportWeeklyMapper), the per-recipient claim (sql/31), the POST, and recording
/// the outcome.
///
/// DRY RUN when FreeDigest:InsightApi:Enabled is false (the endpoint is not configured yet) - see
/// FreeDigestSettings.InsightApiEnabled. The payload is still built from a REAL SP call and a REAL
/// LLM narrative (ComposeInsightJsonActivity runs unconditionally, upstream of this activity) and
/// logged in full, so the whole mechanism - prompt input, LLM output, final JSON shape - can be
/// exercised end-to-end before a real endpoint exists. No claim is taken and nothing is POSTed in
/// this mode, so flipping Enabled on later does not find the week already "used up" by test runs.
///
/// ADR-0003 D5 - response handling, branched on status code BEFORE choosing a deserialization
/// shape (400 is flat, 404 and 200 are wrapped, and "error" is a STRING in the 200 envelope but an
/// OBJECT in the 404 envelope - one target cannot parse both):
///   200 success:true, result.status in {Created, Updated} -> posted, no throw.
///   200 anything else (incl. unparseable)                 -> failed, no throw (closes a live
///                                                             fail-closed gap: EnsureSuccessStatusCode
///                                                             alone would have accepted ANY 200 body).
///   400 {errors:[{field,message}]}                        -> failed, no throw (permanent - retrying
///                                                             an invalid payload changes nothing).
///   401                                                    -> RELEASE claim, throw (systemic
///                                                             misconfiguration, not a per-recipient
///                                                             rejection - retryable once the key is fixed).
///   404 CUSTOMER_USER_NOT_FOUND                            -> failed, no throw (permanent).
///   429                                                    -> joins the transient class (see
///                                                             SendWithRetryAsync); RELEASE + throw
///                                                             if still 429 after every retry.
///   other 4xx                                              -> failed, no throw (unrecognised = permanent).
///   5xx / network, after 15/30/45s retries                 -> RELEASE claim, throw (unchanged).
///
/// ADR-0003 D3 - a wrong WeekEnding can never be caught by the API's own "must be a Monday" check
/// (WeekEnding-6 and WeekEnding+1 are both always Mondays), so this activity asserts the invariant
/// itself: WeekEnding must be a Sunday. A live call on any other day is REFUSED (no claim taken, no
/// throw - see RunAsync) rather than silently upserting into the wrong remote week slot. The dry
/// run still logs the payload even on a wrong day, so it stays useful as the primary dev-loop tool.
/// </summary>
public sealed class PostInsightJsonActivity : AsyncTaskActivity<PostInsightJsonInput, PostInsightJsonOutput>
{
    private const string UpsertPath = "/v2/api/ai-report/weekly/upsert";

    /*  JsonSerializerDefaults.Web leaves the encoder at its strict HTML-safe default, which
        escapes an ordinary apostrophe as "'" (and likewise <, >, &) - defensive for JSON
        embedded in a <script> tag, irrelevant here since this JSON only ever goes over HTTP to
        another API, never into a browser. UnsafeRelaxedJsonEscaping only relaxes THAT escaping -
        it still escapes everything required for valid JSON (quotes, backslashes, control chars) -
        so "don't", "officer's" etc. serialize as plain readable text instead of '-riddled
        prose, both in the dry-run log and in the real POST body. PropertyNameCaseInsensitive on
        the response side only - explicit [JsonPropertyName] attributes own the request/response
        shapes, this just tolerates a differently-cased response without failing to parse it.    */
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions IndentedCamelCase = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };
    private static readonly JsonSerializerOptions ResponseOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    /// <summary>Flat, not geometric - a deliberate product choice for this Kafka-routed endpoint, not DurableTask.Core's built-in backoff shape.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultTransientRetryDelays = [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45)];

    private readonly IInsightJsonRepository _repository;
    private readonly HttpClient _httpClient;
    private readonly FreeDigestSettings _settings;
    private readonly ILogger<PostInsightJsonActivity> _logger;
    private readonly IReadOnlyList<TimeSpan> _transientRetryDelays;

    /// <param name="transientRetryDelays">Overridable only so tests are not forced to wait out real 15/30/45s delays - production always gets <see cref="DefaultTransientRetryDelays"/> (see FreeDigestRegistration).</param>
    public PostInsightJsonActivity(
        IInsightJsonRepository repository, HttpClient httpClient, FreeDigestSettings settings, ILogger<PostInsightJsonActivity> logger,
        IReadOnlyList<TimeSpan>? transientRetryDelays = null)
    {
        _repository = repository;
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
        _transientRetryDelays = transientRetryDelays ?? DefaultTransientRetryDelays;
    }

    protected override Task<PostInsightJsonOutput> ExecuteAsync(TaskContext context, PostInsightJsonInput input) => RunAsync(input);

    internal async Task<PostInsightJsonOutput> RunAsync(PostInsightJsonInput input)
    {
        var weekEnding = DateOnly.ParseExact(input.WeekEnding, "yyyy-MM-dd");
        var periodStartDate = AiReportWeeklyMapper.PeriodStartDateFor(weekEnding);
        var isExpectedWeekShape = weekEnding.DayOfWeek == DayOfWeek.Sunday && periodStartDate.DayOfWeek == DayOfWeek.Monday;

        if (!isExpectedWeekShape && _settings.InsightApiEnabled)
        {
            // ADR-0003 D3: refuse rather than upsert into the wrong remote week slot - the API's
            // own "must be a Monday" validation cannot catch this, because WeekEnding-6 is ALWAYS
            // a Monday regardless of which day WeekEnding itself actually is.
            _logger.LogWarning(
                "PostInsightJsonActivity: tenant {CustomerId} user {UserId} - refusing: WeekEnding {WeekEnding} is not a Sunday, so period_start_date {PeriodStartDate} would not be the intended Monday. Re-run with --FreeDigest:AsOf set to a Sunday.",
                input.CustomerId, input.UserId, input.WeekEnding, periodStartDate);
            return new PostInsightJsonOutput(false, $"refused: WeekEnding {input.WeekEnding} is not a Sunday");
        }

        var payload = AiReportWeeklyMapper.Map(input, periodStartDate);

        if (!_settings.InsightApiEnabled)
        {
            _logger.LogInformation(
                "PostInsightJsonActivity: tenant {CustomerId} user {UserId} - FreeDigest:InsightApi:Enabled is false, DRY RUN (no claim taken, nothing posted). Payload that WOULD be sent:\n{Payload}",
                input.CustomerId, input.UserId, JsonSerializer.Serialize(payload, IndentedCamelCase));
            return new PostInsightJsonOutput(false, "FreeDigest:InsightApi:Enabled is false (dry run - see log for the payload)");
        }

        if (!await _repository.TryClaimPostAsync(input.CustomerId, input.UserId, weekEnding))
            return new PostInsightJsonOutput(false, $"already posted for week ending {input.WeekEnding}");

        try
        {
            using var response = await SendWithRetryAsync(input, payload);
            return await HandleResponseAsync(input, weekEnding, response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PostInsightJsonActivity: tenant {CustomerId} user {UserId} - POST failed after exhausting retries, releasing claim.",
                input.CustomerId, input.UserId);

            await _repository.ReleaseClaimAsync(input.CustomerId, input.UserId, weekEnding);
            throw;
        }
    }

    /// <summary>ADR-0003 D5 - see this class's own doc comment for the full status-code table.</summary>
    private async Task<PostInsightJsonOutput> HandleResponseAsync(PostInsightJsonInput input, DateOnly weekEnding, HttpResponseMessage response)
    {
        var statusCode = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync();

        if (statusCode == 200)
        {
            var success = TryDeserialize<AiReportWeeklySuccessEnvelope>(body);
            if (success is { Success: true } && success.Result?.Status is "Created" or "Updated")
            {
                await _repository.RecordOutcomeAsync(input.CustomerId, input.UserId, weekEnding, "posted");
                return new PostInsightJsonOutput(true, null);
            }

            // A 200 that does NOT carry a confirmed Created/Updated result is a live fail-closed
            // violation if trusted - CLAUDE.md non-negotiable 2. Never assume success from the
            // status code alone.
            await _repository.RecordOutcomeAsync(input.CustomerId, input.UserId, weekEnding, "failed", Truncate($"200 without a confirmed result: {body}"));
            _logger.LogWarning(
                "PostInsightJsonActivity: tenant {CustomerId} user {UserId} - insight API returned 200 but the envelope did not confirm success: {Body}",
                input.CustomerId, input.UserId, body);
            return new PostInsightJsonOutput(false, "insight API returned 200 without a confirmed Created/Updated result");
        }

        if (statusCode == 400)
        {
            var validation = TryDeserialize<AiReportWeeklyValidationErrorEnvelope>(body);
            var detail = validation?.Errors is { Count: > 0 }
                ? string.Join("; ", validation.Errors.Select(e => $"{e.Field}: {e.Message}"))
                : body;

            await _repository.RecordOutcomeAsync(input.CustomerId, input.UserId, weekEnding, "failed", Truncate(detail));
            _logger.LogWarning(
                "PostInsightJsonActivity: tenant {CustomerId} user {UserId} - insight API rejected the payload (400), not retrying: {Detail}",
                input.CustomerId, input.UserId, detail);
            return new PostInsightJsonOutput(false, $"insight API rejected the payload (400): {detail}");
        }

        if (statusCode == 401)
        {
            // Systemic misconfiguration (a bad/rotated API key), not a per-recipient rejection -
            // releasing means the whole tenant's week is retryable once the key is fixed, instead
            // of every recipient's only attempt being burned by one wrong secret.
            throw new HttpRequestException("insight API returned 401 Unauthorized - check FreeDigest:InsightApi:ApiKey");
        }

        if (statusCode == 404)
        {
            var notFound = TryDeserialize<AiReportWeeklyErrorEnvelope>(body);
            var detail = notFound?.Error is { } error ? $"{error.Code}: {error.Message}" : body;

            await _repository.RecordOutcomeAsync(input.CustomerId, input.UserId, weekEnding, "failed", Truncate($"404 {detail}"));
            _logger.LogWarning(
                "PostInsightJsonActivity: tenant {CustomerId} user {UserId} - insight API returned 404, not retrying: {Detail}",
                input.CustomerId, input.UserId, detail);
            return new PostInsightJsonOutput(false, $"insight API returned 404: {detail}");
        }

        if (statusCode == 429)
        {
            // Only reachable here after SendWithRetryAsync has already exhausted every retry and
            // the endpoint is STILL throttling - a self-clearing condition, same treatment as a
            // persistent 5xx, not a permanent per-recipient rejection.
            throw new HttpRequestException("insight API returned 429 Too Many Requests after exhausting retries");
        }

        if (statusCode is >= 400 and < 500)
        {
            await _repository.RecordOutcomeAsync(input.CustomerId, input.UserId, weekEnding, "failed", Truncate(body));
            _logger.LogWarning(
                "PostInsightJsonActivity: tenant {CustomerId} user {UserId} - insight API rejected the payload ({StatusCode}), not retrying: {Body}",
                input.CustomerId, input.UserId, statusCode, body);
            return new PostInsightJsonOutput(false, $"insight API returned {statusCode}");
        }

        // A persistent 5xx that survived every retry in SendWithRetryAsync - throw so RunAsync's
        // catch block releases the claim, same as a network exception.
        response.EnsureSuccessStatusCode();

        // Unreachable: EnsureSuccessStatusCode above throws for anything that is not 2xx, and 200
        // is handled explicitly - kept only so the compiler sees every path return a value.
        await _repository.RecordOutcomeAsync(input.CustomerId, input.UserId, weekEnding, "posted");
        return new PostInsightJsonOutput(true, null);
    }

    private static T? TryDeserialize<T>(string body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, ResponseOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends the payload, retrying a transient failure (network exception, a 5xx, or a 429) at
    /// 15s, 30s, 45s - see this class's own doc comment for why this lives here and not in the
    /// orchestration's RetryOptions. Any other status is returned to the caller immediately,
    /// un-retried - see HandleResponseAsync, which classifies it as permanent or throws. A
    /// request/content must be rebuilt fresh on every attempt: HttpRequestMessage (and the
    /// JsonContent it carries) can only be sent once.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(PostInsightJsonInput input, AiReportWeeklyUpsertRequest payload)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            Exception? failure = null;

            try
            {
                using var request = BuildRequest(payload);
                response = await _httpClient.SendAsync(request);

                if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests)
                    return response;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            var isLastAttempt = attempt == _transientRetryDelays.Count;
            if (isLastAttempt)
            {
                if (failure is not null)
                    throw failure;

                return response!; // a persistent 5xx/429 after every retry - HandleResponseAsync throws for the caller.
            }

            var delay = _transientRetryDelays[attempt];
            _logger.LogWarning(
                "PostInsightJsonActivity: tenant {CustomerId} user {UserId} - insight API attempt {Attempt} of {Total} did not succeed ({Reason}), retrying in {Delay}.",
                input.CustomerId, input.UserId, attempt + 1, _transientRetryDelays.Count + 1,
                failure?.Message ?? $"HTTP {(int)response!.StatusCode}", delay);

            response?.Dispose();
            await Task.Delay(delay);
        }
    }

    private HttpRequestMessage BuildRequest(AiReportWeeklyUpsertRequest payload)
    {
        // ADR-0003 D7: InsightApiUrl is a BASE url - the path is appended by simple string
        // concatenation, not Uri's relative-resolution constructor (new Uri(base, "/path") DISCARDS
        // a base path segment, which would silently break once an environment's base URL carries one).
        var baseUrl = _settings.InsightApiUrl!.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + UpsertPath);

        if (!string.IsNullOrEmpty(_settings.InsightApiKey))
            request.Headers.Add("Authorization", $"Bearer {_settings.InsightApiKey}");

        // Not part of the ai-report-integration.md spec, and the endpoint documents its own
        // upsert-on-key semantics as safe to retry regardless - kept anyway because it costs one
        // header and protects against a gateway in front of it that does honour it. Same
        // (customer, user, period) never produces two distinct bodies.
        request.Headers.Add("Idempotency-Key", $"{payload.CustomerId}-{payload.UserId}-{payload.PeriodStartDate:yyyy-MM-dd}");
        request.Content = JsonContent.Create(payload, options: CamelCase);
        return request;
    }

    private static string Truncate(string value) => value.Length <= 400 ? value : value[..400];
}
