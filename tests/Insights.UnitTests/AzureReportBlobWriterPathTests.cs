using Insights.Domain;
using Insights.Persistence;

namespace Insights.UnitTests;

/// <summary>
/// [PATH LAYOUT 2026-09-10] Report blobs land at
/// <c>&lt;tenantId&gt;/&lt;reportType-slug&gt;/&lt;yyyy&gt;/&lt;mm&gt;/&lt;reportId&gt;.html.enc</c>
/// (was a flat <c>{guid}.dat</c>) so a lifecycle policy can match the yyyy/mm prefix and an
/// offboarding purge is one prefix delete. BuildBlobPath / Slug are pure - test them directly.
/// </summary>
public sealed class AzureReportBlobWriterPathTests
{
    private static readonly Guid ReportId = Guid.Parse("9f3c1e7a-4b2d-4f10-a1c2-e3d4f5b6a7c8");

    [Fact]
    public void BuildBlobPath_ProducesTenantTypeYearMonthReportIdWithHtmlEncExtension()
    {
        var ctx = new BlobPathContext(1300, "fixed_holistic", new DateTime(2026, 9, 7, 23, 30, 0, DateTimeKind.Utc), ReportId);

        var path = AzureReportBlobWriter.BuildBlobPath(ctx);

        Assert.Equal("1300/fixed_holistic/2026/09/9f3c1e7a4b2d4f10a1c2e3d4f5b6a7c8.html.enc", path);
    }

    [Fact]
    public void BuildBlobPath_ZeroPadsTheMonth()
    {
        var ctx = new BlobPathContext(29, "compliance_health", new DateTime(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc), ReportId);

        Assert.StartsWith("29/compliance_health/2027/01/", AzureReportBlobWriter.BuildBlobPath(ctx));
    }

    [Fact]
    public void BuildBlobPath_NormalisesGenerationTimeToUtcBeforeSlicingYearMonth()
    {
        // 2026-04-01 00:30 in a +05:30 zone is still 2026-03-31 19:00 UTC - the path must use UTC.
        var local = new DateTimeOffset(2026, 4, 1, 0, 30, 0, TimeSpan.FromHours(5.5));
        var ctx = new BlobPathContext(1, "act", local.UtcDateTime, ReportId);

        Assert.StartsWith("1/act/2026/03/", AzureReportBlobWriter.BuildBlobPath(ctx));
    }

    [Theory]
    [InlineData("compliance_health", "compliance_health")]
    [InlineData("fixed_holistic", "fixed_holistic")]
    [InlineData("dimension_selection:Licence", "dimension_selection_licence")]
    [InlineData("Dimension Selection - Backlog Aging", "dimension_selection_backlog_aging")]
    [InlineData("  weird//type  ", "weird_type")]
    [InlineData("", "report")]
    [InlineData("!!!", "report")]
    public void Slug_LowercasesAndCollapsesNonAlnumRunsToSingleUnderscore(string input, string expected)
    {
        Assert.Equal(expected, AzureReportBlobWriter.Slug(input));
    }

    [Fact]
    public void BuildBlobPath_CarriesNoPii_OnlyIntTenantSlugAndGuid()
    {
        var path = AzureReportBlobWriter.BuildBlobPath(
            new BlobPathContext(1300, "fixed_holistic", DateTime.UtcNow, ReportId));

        // No spaces, no '@', no uppercase name-like tokens - path segments are id/slug/date/guid only.
        Assert.DoesNotContain(' ', path);
        Assert.DoesNotContain('@', path);
        Assert.Matches(@"^\d+/[a-z0-9_]+/\d{4}/\d{2}/[0-9a-f]{32}\.html\.enc$", path);
    }
}
