using Insights.Data.Email;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// Per-tenant email routing (2026-09-25). The fixture is the real content of
/// dbo.EmailDeliveryGatewayCustomization as shared on 2026-09-25 (UAT), so each expectation below
/// is the actual answer for that tenant - and the edge cases (17's self-pointing switch, 1285's
/// switch with no target, the -1 rows) are real rows, not invented ones.
/// </summary>
public sealed class EmailGatewayRulesTests
{
    private static readonly DateTime Jul2025 = new(2025, 7, 2, 10, 44, 46);

    // ID, CustomerID, EmailGateWayType, EmailFailoverCondition, EmailFailoverSenderID (all IsActive = 1).
    private static readonly EmailGatewayCustomizationRow[] Table =
    [
        Row(1, 5, 1, false, null), Row(2, 23, 2, false, null), Row(3, -1, 1, false, null), Row(4, -1, 2, false, null),
        Row(5, -1, 2, false, null), Row(6, 29, 1, false, null), Row(7, 941, 1, true, 2), Row(8, 17, 1, true, 1),
        Row(9, 1285, 1, true, null), Row(10, 1169, 1, false, null), Row(11, 1363, 1, false, null),
        Row(12, 1380, 1, false, null), Row(13, 1105, 1, false, null), Row(14, 1355, 1, false, null),
        Row(15, 1389, 1, false, null), Row(16, 1424, 1, false, null), Row(17, 1197, 1, false, null),
        Row(18, 1283, 1, false, null), Row(19, 1425, 1, false, null),
    ];

    private static readonly EmailGatewayMasterRow[] Masters =
    [
        new() { ID = 1, IsActive = true },
        new() { ID = 2, IsActive = true },
    ];

    private static EmailGatewayCustomizationRow Row(long id, long customerId, int type, bool failover, int? failoverTo, DateTime? updatedOn = null) => new()
    {
        ID = id, CustomerID = customerId, EmailGateWayType = type,
        EmailFailoverCondition = failover, EmailFailoverSenderID = failoverTo, UpdatedOn = updatedOn ?? Jul2025,
    };

    private static EmailGatewayResolution Resolve(int tenantId, IReadOnlyList<EmailGatewayMasterRow>? masters = null) =>
        EmailGatewayRules.Resolve(tenantId, Table.Where(r => r.CustomerID == tenantId).ToList(), masters ?? Masters, EmailGateway.ElasticEmail);

    [Theory]
    [InlineData(23, EmailGateway.SendGrid, EmailGatewaySource.TenantRow)]        // type 2, no switch
    [InlineData(941, EmailGateway.SendGrid, EmailGatewaySource.FailoverSwitch)]  // type 1, switch -> 2
    [InlineData(5, EmailGateway.ElasticEmail, EmailGatewaySource.TenantRow)]
    [InlineData(29, EmailGateway.ElasticEmail, EmailGatewaySource.TenantRow)]
    [InlineData(1105, EmailGateway.ElasticEmail, EmailGatewaySource.TenantRow)]
    [InlineData(1425, EmailGateway.ElasticEmail, EmailGatewaySource.TenantRow)]
    [InlineData(1082, EmailGateway.ElasticEmail, EmailGatewaySource.Default)]    // no row at all
    [InlineData(0, EmailGateway.ElasticEmail, EmailGatewaySource.Default)]       // the platform's CustomerID=0 fallback is NOT used
    public void TheSharedTableRoutesEachTenantAsAgreed(int tenantId, EmailGateway expected, EmailGatewaySource source)
    {
        var result = Resolve(tenantId);

        Assert.Equal(expected, result.Gateway);
        Assert.Equal(source, result.Source);
        Assert.Empty(result.Warnings);
    }

    /// <summary>1285: switch on, no target - ignored exactly as the platform ignores it, but not silently.</summary>
    [Fact]
    public void ASwitchWithNoTarget_IsIgnoredWithAWarning()
    {
        var result = Resolve(1285);

        Assert.Equal(EmailGateway.ElasticEmail, result.Gateway);
        Assert.Equal(EmailGatewaySource.TenantRow, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("EmailFailoverSenderID is NULL"));
    }

    /// <summary>17: switch on, pointing at its own primary - no effect, warned.</summary>
    [Fact]
    public void ASwitchPointingAtThePrimary_HasNoEffectAndWarns()
    {
        var result = Resolve(17);

        Assert.Equal(EmailGateway.ElasticEmail, result.Gateway);
        Assert.Equal(EmailGatewaySource.TenantRow, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("points at the primary"));
    }

    /// <summary>
    /// The -1 rows are three active rows for one CustomerID (types 1, 2, 2). A real tenant never
    /// has CustomerID -1, but it is the table's only real duplicate, so it proves the tie-break:
    /// latest UpdatedOn wins, and the choice is warned about rather than silent.
    /// </summary>
    [Fact]
    public void SeveralActiveRows_LatestUpdatedOnWins_ThenHighestId()
    {
        var rows = new List<EmailGatewayCustomizationRow>
        {
            Row(3, 77, 1, false, null, new DateTime(2025, 6, 27)),
            Row(4, 77, 2, false, null, new DateTime(2025, 6, 27)),
            Row(5, 77, 1, false, null, new DateTime(2025, 6, 23)),
        };

        var result = EmailGatewayRules.Resolve(77, rows, Masters, EmailGateway.ElasticEmail);

        Assert.Equal(EmailGateway.SendGrid, result.Gateway);          // ID 4: same latest date as ID 3, higher ID
        Assert.Contains(result.Warnings, w => w.Contains("3 active email gateway rows") && w.Contains("row ID 4"));
    }

    [Fact]
    public void AnUnknownGatewayType_RefusesTheTenant_NeverFallsBackToTheDefault()
    {
        var result = EmailGatewayRules.Resolve(50, [Row(1, 50, 7, false, null)], Masters, EmailGateway.ElasticEmail);

        Assert.True(result.IsRefused);
        Assert.Null(result.Gateway);
        Assert.Equal(EmailGatewaySource.Refused, result.Source);
        Assert.Contains("unknown EmailGateWayType 7", result.Detail);
    }

    [Fact]
    public void AnUnknownFailoverTarget_RefusesTheTenant()
    {
        var result = EmailGatewayRules.Resolve(50, [Row(1, 50, 1, true, 9)], Masters, EmailGateway.ElasticEmail);

        Assert.True(result.IsRefused);
        Assert.Contains("unknown EmailFailoverSenderID 9", result.Detail);
    }

    [Fact]
    public void AChosenGatewayWhoseMasterRowIsInactive_RefusesTheTenant()
    {
        var sendGridOff = new[] { new EmailGatewayMasterRow { ID = 1, IsActive = true }, new EmailGatewayMasterRow { ID = 2, IsActive = false } };

        var result = Resolve(941, sendGridOff);

        Assert.True(result.IsRefused);
        Assert.Contains("missing or inactive", result.Detail);
    }

    [Fact]
    public void AChosenGatewayWithNoMasterRow_RefusesTheTenant()
    {
        var result = Resolve(23, [new EmailGatewayMasterRow { ID = 1, IsActive = true }]);

        Assert.True(result.IsRefused);
    }

    /// <summary>A tenant with no row gets the default whatever the master table says - the product rule is simply "no entry = Elastic".</summary>
    [Fact]
    public void NoRow_UsesTheConfiguredDefault()
    {
        var result = EmailGatewayRules.Resolve(1082, [], [], EmailGateway.SendGrid);

        Assert.Equal(EmailGateway.SendGrid, result.Gateway);
        Assert.Equal(EmailGatewaySource.Default, result.Source);
    }

    /// <summary>The enum values ARE EmailGatewayMaster IDs - routing is by ID only, never by GatewayName.</summary>
    [Fact]
    public void GatewayIdsMatchEmailGatewayMaster()
    {
        Assert.Equal(1, (int)EmailGateway.ElasticEmail);
        Assert.Equal(2, (int)EmailGateway.SendGrid);
    }
}
