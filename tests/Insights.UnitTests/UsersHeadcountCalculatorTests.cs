using Insights.Domain;
using Xunit;

namespace Insights.UnitTests;

public class UsersHeadcountCalculatorTests
{
    private static UsersRow Row(int performerInstances, int reviewerInstances) =>
        new() { PerformerInstances = performerInstances, ReviewerInstances = reviewerInstances };

    [Fact]
    public void Compute_CountsUsersWithAtLeastOnePerformerOrReviewerInstance()
    {
        UsersRow[] rows =
        [
            Row(performerInstances: 5, reviewerInstances: 0),   // performer only
            Row(performerInstances: 0, reviewerInstances: 3),   // reviewer only
            Row(performerInstances: 2, reviewerInstances: 4),   // both
            Row(performerInstances: 0, reviewerInstances: 0),   // neither (e.g. "Other" role only)
        ];

        var (performerUserCount, reviewerUserCount) = UsersHeadcountCalculator.Compute(rows);

        Assert.Equal(2, performerUserCount);
        Assert.Equal(2, reviewerUserCount);
    }

    [Fact]
    public void Compute_EmptyRows_ReturnsZeroForBoth()
    {
        var (performerUserCount, reviewerUserCount) = UsersHeadcountCalculator.Compute([]);

        Assert.Equal(0, performerUserCount);
        Assert.Equal(0, reviewerUserCount);
    }
}
