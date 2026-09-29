using System.Text.Json;
using Insights.Agents;

namespace Insights.UnitTests;

public sealed class ReasoningSourceMapTests
{
    public static IEnumerable<object[]> AllFreehandDimensions =>
        [["Act"], ["Users"], ["Location"], ["Departments"], ["BacklogAging"], ["Licence"], ["Risk"], ["Nature"], ["Internal"], ["Event"]];

    [Theory]
    [MemberData(nameof(AllFreehandDimensions))]
    public void EveryFreehandDimension_HasAMapWithChecks(string dimension)
    {
        var map = ReasoningSourceMap.Load(dimension);

        Assert.NotNull(map);
        Assert.Equal(dimension, map.Dimension);
        Assert.NotEmpty(map.Checks);
        Assert.Equal(map.Checks.Count, map.Checks.Select(c => c.Id).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(AllFreehandDimensions))]
    public void CheckQueries_NeverUseSetupVariables(string dimension)
    {
        // Each check runs as its own batch, so a variable declared in the setup is gone by then.
        var map = ReasoningSourceMap.Load(dimension)!;

        Assert.All(map.Checks, c => Assert.DoesNotMatch(@"@\w", c.QueryText));
    }

    [Fact]
    public void PeriodMap_NeedsAPeriod()
    {
        Assert.Null(ReasoningSourceMap.LoadFilledJson("Act", 11416, 1285, null, null));
        // [2026-09-29] Licence follows the report period now (sql/v2/24).
        Assert.Null(ReasoningSourceMap.LoadFilledJson("Licence", 11416, 1285, null, null));
    }

    [Fact]
    public void AsOfTodayMap_LoadsWithoutAPeriod()
    {
        var json = ReasoningSourceMap.LoadFilledJson("BacklogAging", 11416, 1285, null, null);

        Assert.NotNull(json);
        var setup = JsonDocument.Parse(json).RootElement.GetProperty("setup_sql").GetString();
        Assert.Contains("DECLARE @CustomerID INT = 1285;", setup);
        Assert.DoesNotContain("{", setup);
    }
}
