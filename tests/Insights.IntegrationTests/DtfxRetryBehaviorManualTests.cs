using System.Net.Sockets;
using DurableTask.Core;
using DurableTask.SqlServer;
using Xunit.Abstractions;

namespace Insights.IntegrationTests;

/// <summary>
/// EMPIRICAL, not documentation-read: confirms whether DurableTask.Core itself retries an
/// activity that throws a network-shaped exception (LLM timeout / TLS error / connection
/// failure), with and without a ScheduleWithRetry/RetryOptions boundary around the call - the
/// exact question behind InsightsReportOrchestrator's 1.8->1.9 bump (RenderHtmlActivity gained
/// ScheduleWithRetry after three real network failures in one afternoon, 2026-09-01).
///
/// Uses a throwaway orchestrator+activity pair against the REAL SQL-backed TaskHubWorker/
/// SqlOrchestrationService (same InsightsReportOrchestratorManualRunTests.cs pattern) -
/// deliberately NOT a Moq&lt;OrchestrationContext&gt; unit test, because a mock only proves "my
/// orchestrator calls the API I told it to call" - it cannot prove what DurableTask.Core itself
/// does with an unhandled activity exception, which is the actual question here. Costs zero LLM
/// tokens - pure DTFx plumbing, fast, no reason not to run this in full.
///
/// Requires: ConnectionStrings__DurableTaskHub (same as every other manual test in this repo).
/// Run explicitly:
///   dotnet test tests/Insights.IntegrationTests --filter FullyQualifiedName~DtfxRetryBehaviorManualTests
/// </summary>
public sealed class DtfxRetryBehaviorManualTests(ITestOutputHelper output)
{
    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} before running this manual test - see the class doc comment.");

    // ================================================================================
    // The three exception shapes - each one is the REAL chain observed live today against
    // the actual Azure OpenAI endpoint (captured verbatim from this session's own failed runs,
    // not invented), reproduced here so the retry behaviour is tested against what actually
    // happens, not a plausible-looking stand-in.
    // ================================================================================
    private static Exception BuildLlmTimeoutException() =>
        new TaskCanceledException(
            "The operation was cancelled because it exceeded the configured timeout of 0:01:40.",
            new TaskCanceledException("The operation was canceled.",
                new IOException("Unable to read data from the transport connection: The I/O operation has been aborted because of either a thread exit or an application request.",
                    new SocketException((int)SocketError.OperationAborted))));

    private static Exception BuildTlsErrorException() =>
        new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.",
            new System.Security.Authentication.AuthenticationException("The remote certificate is invalid, or the TLS handshake failed.",
                new SocketException((int)SocketError.ConnectionReset)));

    private static Exception BuildConnectionFailureException() =>
        new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));

    public static IEnumerable<object[]> NetworkExceptionShapes()
    {
        yield return ["LLM timeout", (Func<Exception>)BuildLlmTimeoutException];
        yield return ["TLS error", (Func<Exception>)BuildTlsErrorException];
        yield return ["connection failure", (Func<Exception>)BuildConnectionFailureException];
    }

    // ================================================================================
    // Throwaway orchestrator/activity pair. Two orchestrator variants (retry / no-retry) rather
    // than a flag on one, so each is exactly what its name says with nothing conditional to
    // misread mid-investigation.
    // ================================================================================
    private sealed class NoRetryOrchestration : TaskOrchestration<string, string>
    {
        public const string Name = "DtfxRetryProbe_NoRetry";
        public const string Version = "1.0";
        public override async Task<string> RunTask(OrchestrationContext context, string input) =>
            await context.ScheduleTask<string>(typeof(FlakyActivity).Name, "1.0", input);
    }

    private sealed class WithRetryOrchestration : TaskOrchestration<string, string>
    {
        public const string Name = "DtfxRetryProbe_WithRetry";
        public const string Version = "1.0";
        public override async Task<string> RunTask(OrchestrationContext context, string input) =>
            await context.ScheduleWithRetry<string>(typeof(FlakyActivity).Name, "1.0",
                new RetryOptions(TimeSpan.FromSeconds(2), maxNumberOfAttempts: 3) { BackoffCoefficient = 1.0 },
                input);
    }

    /// <summary>
    /// Keyed by the orchestration INSTANCE id, not a single shared static - this class's test
    /// methods can run concurrently (xUnit's default), and a shared counter would let one test's
    /// invocations bleed into another's. FailUntilAttempt=0 means "always fail"; N means "fail
    /// attempts 1..N, then succeed" - proving not just retry-then-fail but real recovery.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> InvocationCounts = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int FailUntilAttempt, Func<Exception> BuildException)> RunConfig = new();

    private sealed class FlakyActivity : AsyncTaskActivity<string, string>
    {
        protected override Task<string> ExecuteAsync(TaskContext context, string input)
        {
            var count = InvocationCounts.AddOrUpdate(input, 1, (_, c) => c + 1);
            var (failUntilAttempt, buildException) = RunConfig[input];
            if (failUntilAttempt == 0 || count <= failUntilAttempt)
                throw buildException();
            return Task.FromResult("recovered on attempt " + count);
        }
    }

    // ================================================================================
    // Test 1 - WITHOUT RetryOptions (plain ScheduleTask), for all three exception shapes: DTFx's
    // default is asserted to be exactly ONE invocation, then the orchestration fails outright.
    // This is the behaviour every activity in this pipeline had BEFORE RenderHtmlActivity's
    // 1.8->1.9 bump - confirms the bug report ("no retry net") was real, not assumed.
    // ================================================================================
    [Theory]
    [MemberData(nameof(NetworkExceptionShapes))]
    public async Task WithoutRetryOptions_NetworkException_FailsAfterExactlyOneInvocation(string label, Func<Exception> buildException)
    {
        var (worker, client) = await StartWorkerAsync();
        try
        {
            worker.AddTaskOrchestrations(new NameValueObjectCreator<TaskOrchestration>(NoRetryOrchestration.Name, NoRetryOrchestration.Version, typeof(NoRetryOrchestration)));
            worker.AddTaskActivities(new NameValueObjectCreator<TaskActivity>(typeof(FlakyActivity).Name, "1.0", typeof(FlakyActivity)));
            await worker.StartAsync();

            var instanceKey = $"no-retry-{label}-{Guid.NewGuid():N}";
            RunConfig[instanceKey] = (0, buildException); // always fail

            var instance = await client.CreateOrchestrationInstanceAsync(NoRetryOrchestration.Name, NoRetryOrchestration.Version, instanceKey);
            var state = await PollUntilTerminalAsync(client, instance.InstanceId, TimeSpan.FromMinutes(1));

            output.WriteLine($"[{label}] status={state.OrchestrationStatus}, invocations={InvocationCounts[instanceKey]}");
            Assert.Equal(OrchestrationStatus.Failed, state.OrchestrationStatus);
            Assert.Equal(1, InvocationCounts[instanceKey]);
        }
        finally
        {
            await worker.StopAsync(true);
        }
    }

    // ================================================================================
    // Test 2 - WITH ScheduleWithRetry: fails on attempts 1-2, SUCCEEDS on attempt 3 (within the
    // configured maxNumberOfAttempts: 3). Proves real end-to-end recovery, not just "retried
    // then failed anyway" - the orchestration reaches Completed, which a mock-based unit test
    // cannot demonstrate since it does not exercise DTFx's own retry loop at all.
    // ================================================================================
    [Theory]
    [MemberData(nameof(NetworkExceptionShapes))]
    public async Task WithScheduleWithRetry_TransientNetworkException_RecoversAndCompletes(string label, Func<Exception> buildException)
    {
        var (worker, client) = await StartWorkerAsync();
        try
        {
            worker.AddTaskOrchestrations(new NameValueObjectCreator<TaskOrchestration>(WithRetryOrchestration.Name, WithRetryOrchestration.Version, typeof(WithRetryOrchestration)));
            worker.AddTaskActivities(new NameValueObjectCreator<TaskActivity>(typeof(FlakyActivity).Name, "1.0", typeof(FlakyActivity)));
            await worker.StartAsync();

            var instanceKey = $"with-retry-{label}-{Guid.NewGuid():N}";
            RunConfig[instanceKey] = (2, buildException); // fail attempts 1 and 2, succeed on 3

            var instance = await client.CreateOrchestrationInstanceAsync(WithRetryOrchestration.Name, WithRetryOrchestration.Version, instanceKey);
            var state = await PollUntilTerminalAsync(client, instance.InstanceId, TimeSpan.FromMinutes(1));

            output.WriteLine($"[{label}] status={state.OrchestrationStatus}, invocations={InvocationCounts[instanceKey]}, output={state.Output}");
            Assert.Equal(OrchestrationStatus.Completed, state.OrchestrationStatus);
            Assert.Equal(3, InvocationCounts[instanceKey]);
            Assert.Contains("recovered on attempt 3", state.Output);
        }
        finally
        {
            await worker.StopAsync(true);
        }
    }

    // ================================================================================
    // Test 3 - WITH ScheduleWithRetry, but the failure never clears (always throws): retries
    // exhaust at maxNumberOfAttempts, THEN the orchestration fails - the honest boundary case.
    // Retry is not infinite and must not be mistaken for one.
    // ================================================================================
    [Fact]
    public async Task WithScheduleWithRetry_PermanentNetworkException_ExhaustsAttemptsThenFails()
    {
        var (worker, client) = await StartWorkerAsync();
        try
        {
            worker.AddTaskOrchestrations(new NameValueObjectCreator<TaskOrchestration>(WithRetryOrchestration.Name, WithRetryOrchestration.Version, typeof(WithRetryOrchestration)));
            worker.AddTaskActivities(new NameValueObjectCreator<TaskActivity>(typeof(FlakyActivity).Name, "1.0", typeof(FlakyActivity)));
            await worker.StartAsync();

            var instanceKey = $"with-retry-exhausted-{Guid.NewGuid():N}";
            RunConfig[instanceKey] = (0, BuildLlmTimeoutException); // always fail

            var instance = await client.CreateOrchestrationInstanceAsync(WithRetryOrchestration.Name, WithRetryOrchestration.Version, instanceKey);
            var state = await PollUntilTerminalAsync(client, instance.InstanceId, TimeSpan.FromMinutes(1));

            output.WriteLine($"status={state.OrchestrationStatus}, invocations={InvocationCounts[instanceKey]}");
            Assert.Equal(OrchestrationStatus.Failed, state.OrchestrationStatus);
            Assert.Equal(3, InvocationCounts[instanceKey]);
        }
        finally
        {
            await worker.StopAsync(true);
        }
    }

    private static async Task<(TaskHubWorker Worker, TaskHubClient Client)> StartWorkerAsync()
    {
        var connectionString = RequireEnv("ConnectionStrings__DurableTaskHub");
        var settings = new SqlOrchestrationServiceSettings(connectionString);
        var service = new SqlOrchestrationService(settings);
        await service.CreateIfNotExistsAsync();
        return (new TaskHubWorker(service), new TaskHubClient(service));
    }

    /// <summary>Same swallow-and-retry-the-poll-itself pattern InsightsReportOrchestratorManualRunTests uses - a transient SqlException on the CLIENT's status poll is a different failure from the orchestration's own.</summary>
    private async Task<OrchestrationState> PollUntilTerminalAsync(TaskHubClient client, string instanceId, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow.Add(budget);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var state = await client.GetOrchestrationStateAsync(instanceId);
                if (state is not null && state.OrchestrationStatus is OrchestrationStatus.Completed or OrchestrationStatus.Failed)
                    return state;
            }
            catch (Exception ex)
            {
                output.WriteLine($"poll error (retrying): {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        throw new TimeoutException($"Instance {instanceId} did not reach a terminal state within {budget}.");
    }
}
