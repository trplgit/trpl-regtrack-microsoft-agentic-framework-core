using Insights.Worker.Orchestration.Activities;

namespace Insights.UnitTests;

/// <summary>
/// The subject line matters more than it looks, because a user can legitimately receive TWO
/// digests in the same week - one per tenant they are mapped to. Without the tenant in the
/// subject they are indistinguishable in an inbox, and the second reads as a duplicate of the
/// first.
/// </summary>
public sealed class DigestSubjectTests
{
    private static readonly DateOnly Week = new(2025, 8, 24);

    /// <summary>THE ONE THAT MATTERS - two tenants, two distinguishable subjects.</summary>
    [Fact]
    public void Subject_DiffersPerTenant_ForTheSameUserAndWeek()
    {
        var a = SendDigestFromArtifactActivity.BuildSubject("Acme Holdings", Week);
        var b = SendDigestFromArtifactActivity.BuildSubject("Beta Foods", Week);

        Assert.NotEqual(a, b);
        Assert.Contains("Acme Holdings", a);
        Assert.Contains("Beta Foods", b);
    }

    [Fact]
    public void Subject_CarriesTheTopicAndMonth()
    {
        var subject = SendDigestFromArtifactActivity.BuildSubject("Acme Holdings", Week);

        Assert.Contains("Laws, August 2025", subject); // 24 Aug 2025 is the 4th Sunday -> the Act (laws) email
        Assert.StartsWith("RegTrack Insights: Acme Holdings", subject);
    }

    /// <summary>
    /// A missing tenant name is a data gap, not a licence to emit "RegTrack Insights:  - week
    /// ...". Falls back to the plain form.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Subject_FallsBackCleanly_WhenTenantNameIsMissing(string? tenantName)
    {
        var subject = SendDigestFromArtifactActivity.BuildSubject(tenantName, Week);

        Assert.Equal("RegTrack Insights - Laws, August 2025", subject);
        Assert.DoesNotContain(":", subject);
    }

    [Fact]
    public void Subject_TrimsSurroundingWhitespaceInTheTenantName()
    {
        var subject = SendDigestFromArtifactActivity.BuildSubject("  Acme Holdings  ", Week);

        Assert.Equal("RegTrack Insights: Acme Holdings - Laws, August 2025", subject);
    }
}
