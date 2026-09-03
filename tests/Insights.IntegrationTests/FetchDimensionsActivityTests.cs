using System.Text.Json;
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
    public async Task RunAsync_Tenant23_ReturnsAllFourteenDimensionsAsJsonStrings()
    {
        var repository = new SqlDimensionRepository(ConnectionString);
        var activity = new FetchDimensionsActivity(repository);

        var result = await activity.RunAsync(new FetchDimensionsInput(36, 23));

        // [FIX, 2026-09-02] Was asserting 9 - already stale before this session's own sql/22-25
        // additions (production FetchDimensionsActivity already called 10, Licence included, by
        // the time this assertion was last touched). Now 14 for real: sql/05, 07-14, 21-25.
        Assert.Equal(14, result.DimensionResults.Count);
        Assert.Contains("Location", result.DimensionResults.Keys);
        Assert.Contains("Risk", result.DimensionResults.Keys);
        // Values are JSON strings, not JsonElement (see FetchDimensionsActivity's doc comment for
        // why - JsonElement does not round-trip through DTFx's Newtonsoft-based DataConverter).
        // Default System.Text.Json naming policy leaves property names as-is (PascalCase).
        var location = JsonSerializer.Deserialize<JsonElement>(result.DimensionResults["Location"]);
        Assert.True(location.TryGetProperty("Dimension", out var dim));
        Assert.Equal("Location", dim.GetString());
        Assert.NotEmpty(result.Assertions);
        Assert.NotEmpty(result.Findings);
    }
}
