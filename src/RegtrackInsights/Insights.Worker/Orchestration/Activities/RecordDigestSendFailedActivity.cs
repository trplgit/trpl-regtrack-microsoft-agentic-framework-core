using DurableTask.Core;
using Insights.Data;
using Microsoft.Extensions.Logging;

namespace Insights.Worker.Orchestration.Activities;

public sealed record RecordDigestSendFailedInput(int TenantId, long UserId, string WeekEnding, string? ProviderUsed, string Detail);

/// <summary>
/// MONDAY: a recipient whose send failed on EVERY retry attempt is recorded as 'failed' and the
/// tenant's run moves on to the next recipient (2026-09-25). Before this, the exhausted retry
/// faulted the whole orchestration and every recipient after the failing one missed the week.
///
/// SendDigestFromArtifactActivity released its claim on each failed attempt, so no log row exists
/// any more - this takes the claim again and writes 'failed' into it. The row is for THIS week
/// only: nothing is suppressed, so the recipient is attempted fresh next week. If the claim is
/// already held (someone else sent or recorded it meanwhile), that record stands and this does
/// nothing.
/// </summary>
public sealed class RecordDigestSendFailedActivity(IFreeDigestRepository repository, ILogger<RecordDigestSendFailedActivity> logger)
    : AsyncTaskActivity<RecordDigestSendFailedInput, bool>
{
    protected override Task<bool> ExecuteAsync(TaskContext context, RecordDigestSendFailedInput input) => RunAsync(input);

    internal async Task<bool> RunAsync(RecordDigestSendFailedInput input)
    {
        var weekEnding = DateOnly.ParseExact(input.WeekEnding, "yyyy-MM-dd");

        logger.LogError(
            "RecordDigestSendFailedActivity: tenant {TenantId} user {UserId} - every send attempt failed; recording failed and moving on. {Detail}",
            input.TenantId, input.UserId, input.Detail);

        if (!await repository.TryClaimSendAsync(input.TenantId, input.UserId, weekEnding))
            return false;

        await repository.RecordOutcomeAsync(input.TenantId, input.UserId, weekEnding, "failed",
            providerUsed: input.ProviderUsed, detail: input.Detail);

        return true;
    }
}
