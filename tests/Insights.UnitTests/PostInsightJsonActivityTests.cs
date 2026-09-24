using System.Net;
using Insights.Data;
using Insights.Domain;
using Insights.Worker;
using Insights.Worker.Orchestration.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// PostInsightJsonActivity's outcomes (ADR-0002, 2026-09-11): disabled lane, lost claim, a
/// permanent 4xx rejection, a transient failure, and the success path - same claim-before-acting
/// shape SendDigestFromArtifactActivityTests already covers for the email lane.
/// </summary>
public sealed class PostInsightJsonActivityTests
{
    /// <summary>A real card from the Users fixture - the activity only needs a well-formed one.</summary>
    private static InsightCard Card()
    {
        var data = MonthlyExamples.Users();
        var input = Insights.Agents.InsightCardInput.Build(data);
        var (headline, narrative) = Insights.Agents.InsightCardFallback.Build(input.Guardrails);
        return Insights.Agents.InsightCardBuilder.Build(input, 1008, 12345, data.Edition.Sunday,
            new Insights.Agents.InsightCardText(headline, narrative, "llm", null, true, 0, 0, string.Empty));
    }

    private static PostInsightJsonInput Input() =>
        new(1008, 12345, "2026-09-13", Card());

    private static FreeDigestSettings Settings(bool enabled = true, HttpStatusCode? statusCode = null) => new()
    {
        FromAddress = "noreply@example.com",
        FromName = "RegTrack Insights",
        UpgradeUrl = "https://example.com/upgrade",
        UnsubscribeBaseUrl = "https://example.com/unsubscribe",
        UnsubscribeSigningKey = "test-key",
        InsightApiEnabled = enabled,
        InsightApiUrl = "https://example.com", // a BASE url (ADR-0003 D7) - the activity appends the upsert path itself
        InsightApiKey = "test-api-key",
    };

    /// <summary>The 200 OK body a real upsert returns - ADR-0003 D5: a 200 must carry this shape to be trusted as "posted", not just the status code.</summary>
    private const string SuccessBody = """{"success":true,"error":"","result":{"status":"Created","aiWeeklyReportID":42,"revisionCount":1,"createdOnUtc":"2026-09-13T10:30:00Z","updatedOnUtc":"2026-09-13T10:30:00Z"}}""";

    // Real production delays are 15s/30s/45s (DefaultTransientRetryDelays) - tests inject
    // millisecond-scale delays instead so the retry PATH is exercised without the suite actually
    // waiting out real time.
    private static readonly IReadOnlyList<TimeSpan> FastRetryDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)];

    private static HttpClient FakeClient(HttpStatusCode statusCode, Exception? throwOnSend = null) =>
        new(new StubHandler(statusCode, throwOnSend)) { BaseAddress = new Uri("https://example.com/") };

    private static HttpClient FakeClientWithBody(HttpStatusCode statusCode, string body) =>
        new(new StubHandler(statusCode, null, body)) { BaseAddress = new Uri("https://example.com/") };

    private static HttpClient FlakyThenOkClient(int failuresBeforeSuccess, HttpStatusCode failureStatus = HttpStatusCode.ServiceUnavailable) =>
        new(new FlakyHandler(failuresBeforeSuccess, failureStatus)) { BaseAddress = new Uri("https://example.com/") };

    private static HttpClient AlwaysThrottledClient() =>
        new(new StubHandler(HttpStatusCode.TooManyRequests, null, "{}")) { BaseAddress = new Uri("https://example.com/") };

    /// <summary>Defaults the body to the real success envelope for a 200 (ADR-0003 D5 requires it to be trusted), "{}" otherwise, unless a body is given explicitly.</summary>
    private sealed class StubHandler(HttpStatusCode statusCode, Exception? throwOnSend, string? body = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (throwOnSend is not null)
                throw throwOnSend;

            var responseBody = body ?? (statusCode == HttpStatusCode.OK ? SuccessBody : "{}");
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody) });
        }
    }

    /// <summary>Fails with a 5xx for the first N sends, then succeeds - simulates the transient blip the 15/30/45s retry exists to ride out.</summary>
    private sealed class FlakyHandler(int failuresBeforeSuccess, HttpStatusCode failureStatus) : HttpMessageHandler
    {
        private int _attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _attempts++;
            var statusCode = _attempts <= failuresBeforeSuccess ? failureStatus : HttpStatusCode.OK;
            var responseBody = statusCode == HttpStatusCode.OK ? SuccessBody : "{}";
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody) });
        }
    }

    /// <summary>Captures the outgoing request body so a test can inspect exactly what was serialized onto the wire.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    [Fact]
    public async Task WhenDisabled_ItPostsNothingAndNeverTouchesTheClaim()
    {
        var repo = new Mock<IInsightJsonRepository>();
        var activity = new PostInsightJsonActivity(repo.Object, FakeClient(HttpStatusCode.OK), Settings(enabled: false), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        var result = await activity.RunAsync(Input());

        Assert.False(result.Posted);
        Assert.Contains("Enabled is false", result.Reason);
        repo.Verify(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenTheClaimIsLost_ItPostsNothing()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var activity = new PostInsightJsonActivity(repo.Object, FakeClient(HttpStatusCode.OK), Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        var result = await activity.RunAsync(Input());

        Assert.False(result.Posted);
        Assert.Contains("already posted", result.Reason);
    }

    [Fact]
    public async Task WhenTheClaimIsWonAndThePostSucceeds_ItRecordsPosted()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var activity = new PostInsightJsonActivity(repo.Object, FakeClient(HttpStatusCode.OK), Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        var result = await activity.RunAsync(Input());

        Assert.True(result.Posted);
        repo.Verify(r => r.RecordOutcomeAsync(1008, 12345, It.IsAny<DateOnly>(), "posted", null, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.ReleaseClaimAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A 4xx is a permanent rejection - recorded as failed, never released, never rethrown, so the orchestration's retry budget is not wasted on something that can never succeed.</summary>
    [Fact]
    public async Task WhenTheApiRejectsWith4xx_ItRecordsFailedAndDoesNotThrow()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var activity = new PostInsightJsonActivity(repo.Object, FakeClient(HttpStatusCode.BadRequest), Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        var result = await activity.RunAsync(Input());

        Assert.False(result.Posted);
        repo.Verify(r => r.RecordOutcomeAsync(1008, 12345, It.IsAny<DateOnly>(), "failed", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.ReleaseClaimAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A transient failure hands the claim back so a retry within the week is possible - same shape as SendDigestFromArtifactActivity's release-on-failure.</summary>
    [Fact]
    public async Task WhenThePostThrows_ItReleasesTheClaimAndRethrows()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var activity = new PostInsightJsonActivity(
            repo.Object, FakeClient(HttpStatusCode.OK, throwOnSend: new HttpRequestException("provider down")), Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        await Assert.ThrowsAsync<HttpRequestException>(() => activity.RunAsync(Input()));

        repo.Verify(r => r.ReleaseClaimAsync(1008, 12345, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The point of the 15/30/45s retry: a 5xx that clears within the retry window must still end in success, not a release-and-rethrow.</summary>
    [Fact]
    public async Task WhenA5xxClearsWithinTheRetryWindow_ItStillSucceeds()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var activity = new PostInsightJsonActivity(
            repo.Object, FlakyThenOkClient(failuresBeforeSuccess: 2), Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        var result = await activity.RunAsync(Input());

        Assert.True(result.Posted);
        repo.Verify(r => r.RecordOutcomeAsync(1008, 12345, It.IsAny<DateOnly>(), "posted", null, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.ReleaseClaimAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A 5xx that never clears exhausts all three retries (four attempts total) before the activity gives up and releases the claim.</summary>
    [Fact]
    public async Task WhenA5xxNeverClears_ItExhaustsAllRetriesThenReleasesAndThrows()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var activity = new PostInsightJsonActivity(
            repo.Object, FlakyThenOkClient(failuresBeforeSuccess: int.MaxValue), Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        await Assert.ThrowsAsync<HttpRequestException>(() => activity.RunAsync(Input()));

        repo.Verify(r => r.ReleaseClaimAsync(1008, 12345, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>The POSTed body must read as plain text, not over-escaped for HTML - an apostrophe in the LLM's prose must serialize as ' , not the ' the default Web JSON encoder would produce.</summary>
    [Fact]
    public async Task ThePostedBody_DoesNotOverEscapeApostrophes()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = new CapturingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };
        var activity = new PostInsightJsonActivity(repo.Object, client, Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        var card = Card() with { Headline = "1 of the organisation's 24 obligations carry personal liability" };
        var input = new PostInsightJsonInput(1008, 12345, "2026-09-13", card);

        await activity.RunAsync(input);

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("organisation's", handler.LastRequestBody);
        Assert.DoesNotContain("\\u0027", handler.LastRequestBody);
    }

    /// <summary>Production must always get the real 15s/30s/45s sequence, not the test override.</summary>
    [Fact]
    public void DefaultTransientRetryDelays_AreFifteenThirtyFortyFiveSeconds()
    {
        Assert.Equal(
            [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45)],
            PostInsightJsonActivity.DefaultTransientRetryDelays);
    }

    /// <summary>ADR-0003 D5: a 200 status code alone is not enough - the envelope must confirm success, or nothing is recorded as posted.</summary>
    [Fact]
    public async Task WhenTheApiReturns200WithoutAConfirmedResult_ItRecordsFailedAndDoesNotThrow()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var activity = new PostInsightJsonActivity(
            repo.Object, FakeClientWithBody(HttpStatusCode.OK, """{"success":false,"error":"","result":{}}"""), Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        var result = await activity.RunAsync(Input());

        Assert.False(result.Posted);
        repo.Verify(r => r.RecordOutcomeAsync(1008, 12345, It.IsAny<DateOnly>(), "failed", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.ReleaseClaimAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>ADR-0003 D5: 404 CUSTOMER_USER_NOT_FOUND is a permanent, wrapped-with-an-object-error rejection - failed, never released, never rethrown.</summary>
    [Fact]
    public async Task WhenTheApiReturns404_ItRecordsFailedAndDoesNotThrow()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        const string notFoundBody = """{"success":false,"error":{"code":"CUSTOMER_USER_NOT_FOUND","message":"No matching, active user was found for this customer_id/user_id."},"result":{"status":"NotFound"}}""";
        var activity = new PostInsightJsonActivity(
            repo.Object, FakeClientWithBody(HttpStatusCode.NotFound, notFoundBody), Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        var result = await activity.RunAsync(Input());

        Assert.False(result.Posted);
        repo.Verify(r => r.RecordOutcomeAsync(1008, 12345, It.IsAny<DateOnly>(), "failed", It.Is<string>(d => d.Contains("CUSTOMER_USER_NOT_FOUND")), It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.ReleaseClaimAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>ADR-0003 D5: 401 is systemic misconfiguration, not a per-recipient rejection - unlike every other 4xx, it releases the claim so the whole week is retryable once the key is fixed.</summary>
    [Fact]
    public async Task WhenTheApiReturns401_ItReleasesTheClaimAndThrows()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var activity = new PostInsightJsonActivity(
            repo.Object, FakeClient(HttpStatusCode.Unauthorized), Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        await Assert.ThrowsAsync<HttpRequestException>(() => activity.RunAsync(Input()));

        repo.Verify(r => r.ReleaseClaimAsync(1008, 12345, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>ADR-0003 D5: 429 joins the transient class - it must be retried like a 5xx, and only releases/throws once every retry is exhausted and it is still throttling.</summary>
    [Fact]
    public async Task WhenTheApiIsPersistentlyThrottled_ItExhaustsRetriesThenReleasesAndThrows()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var activity = new PostInsightJsonActivity(
            repo.Object, AlwaysThrottledClient(), Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        await Assert.ThrowsAsync<HttpRequestException>(() => activity.RunAsync(Input()));

        repo.Verify(r => r.ReleaseClaimAsync(1008, 12345, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>ADR-0003 D3: WeekEnding must be a Sunday or a live POST is refused outright - the API's own "must be a Monday" check cannot catch this (WeekEnding-6 is always a Monday regardless of what WeekEnding itself is).</summary>
    [Fact]
    public async Task WhenLiveAndWeekEndingIsNotASunday_ItRefusesWithoutClaimingOrPosting()
    {
        var repo = new Mock<IInsightJsonRepository>();
        var activity = new PostInsightJsonActivity(repo.Object, FakeClient(HttpStatusCode.OK), Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        // 2026-09-14 is a Monday, not the Sunday WeekEnding is supposed to be.
        var input = Input() with { WeekEnding = "2026-09-14" };
        var result = await activity.RunAsync(input);

        Assert.False(result.Posted);
        Assert.Contains("not a Sunday", result.Reason);
        repo.Verify(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>ADR-0003 D2 (user-confirmed 2026-09-14): the report covers the LAST seven days, same as the email - period_start_date is the Monday that STARTS the week WeekEnding closes (WeekEnding-6), never WeekEnding+1.</summary>
    [Fact]
    public async Task ThePostedBody_UsesWeekEndingMinusSixAsThePeriodStartDate()
    {
        var repo = new Mock<IInsightJsonRepository>();
        repo.Setup(r => r.TryClaimPostAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = new CapturingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };
        var activity = new PostInsightJsonActivity(repo.Object, client, Settings(), NullLogger<PostInsightJsonActivity>.Instance, FastRetryDelays);

        // WeekEnding 2026-09-13 is a Sunday; the week it closes runs Mon 2026-09-07 - Sun 2026-09-13.
        var input = Input() with { WeekEnding = "2026-09-13" };
        await activity.RunAsync(input);

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"period_start_date\":\"2026-09-07\"", handler.LastRequestBody);
    }
}
