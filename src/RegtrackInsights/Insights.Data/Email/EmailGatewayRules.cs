namespace Insights.Data.Email;

/// <summary>One active <c>dbo.EmailDeliveryGatewayCustomization</c> row. Only the columns routing reads.</summary>
public sealed class EmailGatewayCustomizationRow
{
    public long ID { get; set; }
    public long CustomerID { get; set; }
    public int EmailGateWayType { get; set; }
    public bool? EmailFailoverCondition { get; set; }
    public int? EmailFailoverSenderID { get; set; }
    public DateTime? UpdatedOn { get; set; }
}

/// <summary>One <c>dbo.EmailGatewayMaster</c> row. Only the columns routing reads.</summary>
public sealed class EmailGatewayMasterRow
{
    public int ID { get; set; }
    public bool IsActive { get; set; }
}

public enum EmailGatewaySource
{
    /// <summary>The tenant has no active row - the configured default (Elastic Email).</summary>
    Default,

    /// <summary>The tenant's own <c>EmailGateWayType</c>.</summary>
    TenantRow,

    /// <summary>The ops switch: <c>EmailFailoverCondition = 1</c> with a valid <c>EmailFailoverSenderID</c>.</summary>
    FailoverSwitch,

    /// <summary>The tenant's configuration cannot be trusted - nothing is sent.</summary>
    Refused,
}

public sealed record EmailGatewayResolution(
    EmailGateway? Gateway, EmailGatewaySource Source, string Detail, IReadOnlyList<string> Warnings)
{
    public bool IsRefused => Gateway is null;
}

/// <summary>
/// Which provider a tenant's free digest goes out through. Pure - the SQL resolver only fetches
/// rows and hands them here, so every rule below is unit-tested without a database.
///
/// Mirrors how RegTrack 1.0 reads the same tables (ComplianceManagement.GetEmailGateWayDetails),
/// with three deliberate differences:
/// <list type="bullet">
///   <item>No <c>CustomerID = 0</c> fallback row - a tenant without its own active row gets the
///   configured default (Elastic Email), per the product decision.</item>
///   <item>Several active rows for one tenant are ordered deterministically (latest UpdatedOn,
///   then highest ID) instead of the platform's unordered FirstOrDefault.</item>
///   <item>An unknown gateway ID, or one whose master row is missing/inactive, REFUSES the tenant
///   instead of silently sending nothing or guessing a provider (fail closed, fail loudly).</item>
/// </list>
///
/// <c>EmailFailoverCondition = 1</c> is an ops SWITCH, not try-then-fall-back: the primary is not
/// attempted at all. A switch with no target, or pointing at the primary itself, is ignored with a
/// warning - that is exactly how the platform behaves for the no-target case.
/// </summary>
public static class EmailGatewayRules
{
    public static EmailGatewayResolution Resolve(
        int tenantId,
        IReadOnlyList<EmailGatewayCustomizationRow> activeTenantRows,
        IReadOnlyList<EmailGatewayMasterRow> masterRows,
        EmailGateway defaultGateway)
    {
        var warnings = new List<string>();

        if (activeTenantRows.Count == 0)
            return new EmailGatewayResolution(defaultGateway, EmailGatewaySource.Default,
                $"Tenant {tenantId} has no active email gateway row - using the default ({defaultGateway}).", warnings);

        var row = activeTenantRows
            .OrderByDescending(r => r.UpdatedOn ?? DateTime.MinValue)
            .ThenByDescending(r => r.ID)
            .First();

        if (activeTenantRows.Count > 1)
            warnings.Add($"Tenant {tenantId} has {activeTenantRows.Count} active email gateway rows - using row ID {row.ID} (latest UpdatedOn, then highest ID).");

        if (!Enum.IsDefined(typeof(EmailGateway), row.EmailGateWayType))
            return Refuse(tenantId, $"row ID {row.ID} has unknown EmailGateWayType {row.EmailGateWayType}.", warnings);

        var primary = (EmailGateway)row.EmailGateWayType;
        var chosen = primary;
        var source = EmailGatewaySource.TenantRow;

        if (row.EmailFailoverCondition == true)
        {
            if (row.EmailFailoverSenderID is not { } target)
            {
                warnings.Add($"Tenant {tenantId} row ID {row.ID}: EmailFailoverCondition is set but EmailFailoverSenderID is NULL - switch ignored, using {primary}.");
            }
            else if (!Enum.IsDefined(typeof(EmailGateway), target))
            {
                return Refuse(tenantId, $"row ID {row.ID} has unknown EmailFailoverSenderID {target}.", warnings);
            }
            else if ((EmailGateway)target == primary)
            {
                warnings.Add($"Tenant {tenantId} row ID {row.ID}: EmailFailoverSenderID points at the primary ({primary}) - switch has no effect.");
            }
            else
            {
                chosen = (EmailGateway)target;
                source = EmailGatewaySource.FailoverSwitch;
            }
        }

        if (!masterRows.Any(m => m.ID == (int)chosen && m.IsActive))
            return Refuse(tenantId, $"EmailGatewayMaster ID {(int)chosen} ({chosen}) is missing or inactive.", warnings);

        var detail = source == EmailGatewaySource.FailoverSwitch
            ? $"Tenant {tenantId}: failover switch on row ID {row.ID} routes {primary} -> {chosen}."
            : $"Tenant {tenantId}: row ID {row.ID} routes to {chosen}.";

        return new EmailGatewayResolution(chosen, source, detail, warnings);
    }

    private static EmailGatewayResolution Refuse(int tenantId, string why, List<string> warnings) =>
        new(null, EmailGatewaySource.Refused, $"Tenant {tenantId} email gateway configuration refused: {why}", warnings);
}
