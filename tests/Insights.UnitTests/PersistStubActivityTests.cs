using Insights.Worker.Orchestration.Activities;
using Xunit;

namespace Insights.UnitTests;

public class PersistStubActivityTests
{
    [Fact]
    public async Task RunAsync_ReturnsANonEmptyArtifactId()
    {
        var activity = new PersistStubActivity();
        var result = await activity.RunAsync(new PersistStubInput("<html></html>", 29, "compliance_health"));

        Assert.False(string.IsNullOrWhiteSpace(result.ArtifactId));
    }
}
