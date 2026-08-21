using Insights.Domain;
using Insights.Worker.Orchestration;
using Xunit;

namespace Insights.UnitTests;

public class InsightsReportOrchestrationInputTests
{
    [Fact]
    public void TenantScopeRequest_RoundTripsThroughJson()
    {
        var input = new InsightsReportOrchestrationInput(
            TenantId: 29,
            ReportType: "compliance_health",
            Scope: new InsightsScopeRequest("tenant", EntityId: null),
            Period: "FY2025-26",
            UserId: 38);

        var json = System.Text.Json.JsonSerializer.Serialize(input);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<InsightsReportOrchestrationInput>(json);

        Assert.Equal(input, roundTripped);
    }
}
