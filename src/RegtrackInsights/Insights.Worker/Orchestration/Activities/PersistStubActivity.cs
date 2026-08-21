using DurableTask.Core;

namespace Insights.Worker.Orchestration.Activities;

public sealed record PersistStubInput(string Html, int TenantId, string ReportType);
public sealed record PersistStubOutput(string ArtifactId);

/// <summary>
/// Node 12 STUB - build order item 14 (persistence: blob + index, envelope encryption, view-time
/// re-auth, short SAS) replaces this body with the real thing. Input shape is deliberately already
/// what real persistence needs (html + tenant + report type) so that swap changes only this
/// class's body, nothing upstream.
/// </summary>
public sealed class PersistStubActivity : AsyncTaskActivity<PersistStubInput, PersistStubOutput>
{
    protected override Task<PersistStubOutput> ExecuteAsync(TaskContext context, PersistStubInput input) => RunAsync(input);

    internal Task<PersistStubOutput> RunAsync(PersistStubInput input)
    {
        var artifactId = $"stub-{input.TenantId}-{input.ReportType}-{Guid.NewGuid():N}";
        Console.WriteLine($"[PersistStubActivity] Would persist {input.Html.Length} chars for tenant {input.TenantId} as {artifactId} (item 14 not built yet).");
        return Task.FromResult(new PersistStubOutput(artifactId));
    }
}
