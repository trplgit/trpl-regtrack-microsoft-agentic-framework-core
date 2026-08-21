using Insights.Data;
using Insights.Worker.Orchestration.Activities;
using Xunit;

namespace Insights.IntegrationTests;

public class FetchDimensionsActivityTests
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__RegTrack")
        ?? throw new InvalidOperationException("Set ConnectionStrings__RegTrack before running this test.");

    [Fact]
    public async Task RunAsync_Tenant23_ReturnsAllNineDimensionsAsJsonElements()
    {
        var repository = new SqlDimensionRepository(ConnectionString);
        var activity = new FetchDimensionsActivity(repository);

        var result = await activity.RunAsync(new FetchDimensionsInput(36, 23));

        Assert.Equal(9, result.DimensionResults.Count);
        Assert.Contains("Location", result.DimensionResults.Keys);
        Assert.Contains("Risk", result.DimensionResults.Keys);
        // Default System.Text.Json naming policy leaves property names as-is (PascalCase) - the
        // key is "Dimension", not "dimension", since SerializeToElement is called with no options.
        Assert.True(result.DimensionResults["Location"].TryGetProperty("Dimension", out var dim));
        Assert.Equal("Location", dim.GetString());
        Assert.NotEmpty(result.Assertions);
        Assert.NotEmpty(result.Findings);
    }
}
