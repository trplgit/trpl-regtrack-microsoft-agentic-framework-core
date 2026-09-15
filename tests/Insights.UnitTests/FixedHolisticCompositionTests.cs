using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>Pins the fixed 6-block order - no LLM/DB needed, matches the real product UI's fixed tab order exactly.</summary>
public sealed class FixedHolisticCompositionTests
{
    [Fact]
    public void Build_ReturnsTheSixBlocksInFixedOrder_EveryTime()
    {
        var plan = FixedHolisticComposition.Build();

        Assert.Equal("snapshot", plan.Hero.Block);
        Assert.Equal(
            ["snapshot", "risk_licences", "coverage", "operations", "forward_look", "actions"],
            plan.Blocks.Select(b => b.Block));
        Assert.Empty(plan.Omitted);
    }

    [Fact]
    public void Build_IsDeterministic_CalledTwiceProducesIdenticalBlockOrder()
    {
        var first = FixedHolisticComposition.Build();
        var second = FixedHolisticComposition.Build();

        Assert.Equal(first.Blocks.Select(b => b.Block), second.Blocks.Select(b => b.Block));
    }
}
