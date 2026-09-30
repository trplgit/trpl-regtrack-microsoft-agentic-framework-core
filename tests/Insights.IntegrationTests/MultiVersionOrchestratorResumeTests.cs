using DurableTask.Core;
using Microsoft.Data.SqlClient;
using DurableTask.SqlServer;
using Xunit;

namespace Insights.IntegrationTests;

public class MultiVersionOrchestratorResumeTests : IAsyncLifetime
{
    private readonly string _databaseName = $"InsightsMultiVersionResumeTest_{Guid.NewGuid():N}";
    private string _masterConnectionString = "";
    private string _hubConnectionString = "";

    public async Task InitializeAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DurableTaskHub")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DurableTaskHub before running this test.");
        var builder = new SqlConnectionStringBuilder(baseConnectionString);
        builder.InitialCatalog = "master";
        _masterConnectionString = builder.ConnectionString;
        builder.InitialCatalog = _databaseName;
        _hubConnectionString = builder.ConnectionString;

        await using var master = new SqlConnection(_masterConnectionString);
        await master.OpenAsync();
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{_databaseName}]";
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var master = new SqlConnection(_masterConnectionString);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText = $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];";
        await drop.ExecuteNonQueryAsync();
    }

    private sealed class OldVersionOrchestration : TaskOrchestration<string, string>
    {
        public override async Task<string> RunTask(OrchestrationContext context, string input)
        {
            var echoed = await context.ScheduleTask<string>("Echo", "1.0", input);
            return $"old-version-result:{echoed}";
        }
    }

    private sealed class NewVersionOrchestration : TaskOrchestration<string, string>
    {
        public override async Task<string> RunTask(OrchestrationContext context, string input)
        {
            var echoed = await context.ScheduleTask<string>("Echo", "1.0", input);
            return $"new-version-result:{echoed}";
        }
    }

    private sealed class EchoActivity : TaskActivity<string, string>
    {
        protected override string Execute(TaskContext context, string input) => input;
    }

    private SqlOrchestrationService BuildService() => new(new SqlOrchestrationServiceSettings(_hubConnectionString)
    {
        MaxConcurrentActivities = 1,
        MaxActiveOrchestrations = 1,
    });

    [Fact]
    public async Task OldVersionInstance_StillPendingAfterFirstWorkerStops_CompletesViaDualRegisteredWorker()
    {
        const string orchestratorName = "MultiVersionResumeTestOrchestrator";
        const string instanceId = "multi-version-resume-test-instance";

        var serviceA = BuildService();
        await serviceA.CreateIfNotExistsAsync();

        // Worker A: only the OLD version registered - simulates the pre-deploy pod.
        var workerA = new TaskHubWorker(serviceA);
        workerA.AddTaskOrchestrations(new NameValueObjectCreator<TaskOrchestration>(
            orchestratorName, "1.0", typeof(OldVersionOrchestration)));
        workerA.AddTaskActivities(new NameValueObjectCreator<TaskActivity>("Echo", "1.0", typeof(EchoActivity)));

        var clientA = new TaskHubClient((IOrchestrationServiceClient)serviceA);
        await clientA.CreateOrchestrationInstanceAsync(orchestratorName, "1.0", instanceId, "hello");

        // Confirm it's still Pending before any worker starts - this is what makes the later
        // "completes via Worker B" assertion actually mean something (see this task's own intro).
        var stateBeforeAnyWorkerRuns = await clientA.GetOrchestrationStateAsync(instanceId);
        Assert.Equal(OrchestrationStatus.Pending, stateBeforeAnyWorkerRuns.OrchestrationStatus);

        // Worker A never starts its dispatch loop at all in this test - a Pending instance with no
        // worker running is exactly "queued across a deploy boundary, old pod already gone."

        // Worker B: BOTH versions registered - simulates the post-deploy pod carrying the frozen
        // old-version class alongside its own current one.
        var serviceB = BuildService();
        var workerB = new TaskHubWorker(serviceB);
        workerB.AddTaskOrchestrations(
            new NameValueObjectCreator<TaskOrchestration>(orchestratorName, "1.0", typeof(OldVersionOrchestration)),
            new NameValueObjectCreator<TaskOrchestration>(orchestratorName, "2.0", typeof(NewVersionOrchestration)));
        workerB.AddTaskActivities(new NameValueObjectCreator<TaskActivity>("Echo", "1.0", typeof(EchoActivity)));

        await workerB.StartAsync();
        try
        {
            var clientB = new TaskHubClient((IOrchestrationServiceClient)serviceB);
            var finalState = await clientB.WaitForOrchestrationAsync(
                new OrchestrationInstance { InstanceId = instanceId }, TimeSpan.FromSeconds(30));

            Assert.Equal(OrchestrationStatus.Completed, finalState.OrchestrationStatus);
            // The OLD version's type produced this, not the new one - proves Worker B actually
            // dispatched by the instance's own recorded version ("1.0"), not just its own current one.
            Assert.Equal("\"old-version-result:hello\"", finalState.Output);
        }
        finally
        {
            await workerB.StopAsync();
        }
    }
}
