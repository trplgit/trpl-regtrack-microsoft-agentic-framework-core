namespace Insights.Data.Email;

/// <summary>
/// The email providers a tenant can be routed to. The numeric values ARE <c>EmailGatewayMaster.ID</c>
/// - routing is decided by ID only, never by <c>GatewayName</c> (same ID-only rule as statuses).
/// An ID not listed here is a configuration error and refuses the tenant; it never falls back.
/// </summary>
public enum EmailGateway
{
    ElasticEmail = 1,
    SendGrid = 2,
}
