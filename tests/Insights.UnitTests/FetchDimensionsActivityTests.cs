using Insights.Data;
using Insights.Domain;
using Insights.Worker.Orchestration.Activities;
using Moq;

namespace Insights.UnitTests;

/// <summary>
/// Design doc Sec.11.4 (Partial generation). Unit-level, mocked IDimensionRepository - the real
/// fifteen-dimension success path against UAT is covered separately by
/// Insights.IntegrationTests.FetchDimensionsActivityTests. These pin the CATCH logic itself:
/// exactly which two exception types get degraded to a placeholder slot, which two still fail the
/// whole run, and that a failed dimension's data never reaches DimensionResults/Assertions/
/// Findings in any form.
/// </summary>
public sealed class FetchDimensionsActivityTests
{
    private const int UserId = 38;
    private const int TenantId = 29;

    private static Mock<IDimensionRepository> BuildHealthyRepository()
    {
        var repo = new Mock<IDimensionRepository>();
        repo.Setup(r => r.GetLocationAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<LocationControlTotals, LocationRow>("Location", new LocationControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetEntityAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<EntityControlTotals, EntityRow>("Entity",
                new EntityControlTotals(0, 0, true, 0, 0, 0, 0, 0, EntityCountShape.SingleEntity, 0, ComparisonGrain.Locations, ""), [], [], [], [], []));
        repo.Setup(r => r.GetRiskAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<RiskControlTotals, RiskRow>("Risk", new RiskControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetNatureAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<NatureControlTotals, NatureRow>("Nature", new NatureControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetDepartmentsAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<DepartmentsControlTotals, DepartmentsRow>("Departments", new DepartmentsControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetActAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<ActControlTotals, ActRow>("Act", new ActControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetUsersAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<UsersControlTotals, UsersRow>("Users", new UsersControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetInternalAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<InternalControlTotals, InternalRow>("Internal", new InternalControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetEventAsync(UserId, TenantId, null, 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<EventControlTotals, EventRow>("Event", new EventControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetLicenceAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<LicenceControlTotals, LicenceRow>("Licence", new LicenceControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetBacklogAgingAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<BacklogAgingControlTotals, BacklogAgingRow>("BacklogAging", new BacklogAgingControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetTimelinessFYAsync(UserId, TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<TimelinessFYControlTotals, TimelinessFYRow>("TimelinessFY", new TimelinessFYControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetForwardPipelineAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<ForwardPipelineControlTotals, ForwardPipelineRow>("ForwardPipeline", new ForwardPipelineControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetEvidenceIntegrityAsync(UserId, TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<EvidenceIntegrityControlTotals, EvidenceIntegrityRow>("EvidenceIntegrity", new EvidenceIntegrityControlTotals(), [], [], [], [], []));
        repo.Setup(r => r.GetForwardRiskAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<ForwardRiskControlTotals, ForwardRiskRow>("ForwardRisk", new ForwardRiskControlTotals(), [], [], [], [], []));
        return repo;
    }

    [Fact]
    public async Task RunAsync_AllFifteenSucceed_ReturnsEmptyFailedDimensions()
    {
        var repo = BuildHealthyRepository();
        var activity = new FetchDimensionsActivity(repo.Object);

        var result = await activity.RunAsync(new FetchDimensionsInput(UserId, TenantId));

        Assert.Equal(15, result.DimensionResults.Count);
        Assert.Empty(result.FailedDimensions);
    }

    [Theory]
    [InlineData(typeof(DimensionReconciliationException))]
    [InlineData(typeof(DimensionContractViolationException))]
    public async Task RunAsync_OneDimensionThrowsAPartialClassException_IsolatesItToFailedDimensions(Type exceptionType)
    {
        var repo = BuildHealthyRepository();
        var exception = exceptionType == typeof(DimensionReconciliationException)
            ? new DimensionReconciliationException("Risk", TenantId, new Exception("inner"))
            : (Exception)new DimensionContractViolationException("Risk", "detector flagged > eligible");
        repo.Setup(r => r.GetRiskAsync(UserId, TenantId, null, It.IsAny<CancellationToken>())).ThrowsAsync(exception);

        var recorder = new Mock<IDimensionFailureRecorder>();
        var activity = new FetchDimensionsActivity(repo.Object, recorder.Object);

        var result = await activity.RunAsync(new FetchDimensionsInput(UserId, TenantId));

        Assert.Equal(14, result.DimensionResults.Count);
        Assert.DoesNotContain("Risk", result.DimensionResults.Keys);
        Assert.Equal(["Risk"], result.FailedDimensions);
        recorder.Verify(r => r.RecordBlockFailure("Risk"), Times.Once);
    }

    /// <summary>Confirms the failed dimension's own assertions/findings never leak into the aggregated lists either - not just its dictionary entry.</summary>
    [Fact]
    public async Task RunAsync_FailedDimension_ContributesNoAssertionsOrFindings()
    {
        var repo = BuildHealthyRepository();
        // Override Location with one that carries a real assertion/finding, so the test can prove
        // successful dimensions' data DOES flow through, while Entity's (thrown) does not.
        repo.Setup(r => r.GetLocationAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DimensionResult<LocationControlTotals, LocationRow>(
                "Location", new LocationControlTotals(), [],
                [], [new Assertion("A1", "overdue_pct", "tenant", 10m, null, null, null, null, null, null)],
                [new Finding("F1", FindingSeverity.Medium, "headline", ["A1"], null)], []));
        repo.Setup(r => r.GetEntityAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("Entity", TenantId, new Exception("inner")));

        var activity = new FetchDimensionsActivity(repo.Object);

        var result = await activity.RunAsync(new FetchDimensionsInput(UserId, TenantId));

        Assert.Single(result.Assertions); // only Location's
        Assert.Single(result.Findings);
        Assert.Equal(["Entity"], result.FailedDimensions);
    }

    [Fact]
    public async Task RunAsync_ScopeDenied_PropagatesUncaught_FailsTheWholeRun()
    {
        var repo = BuildHealthyRepository();
        repo.Setup(r => r.GetRiskAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionScopeDeniedException("Risk", UserId, TenantId, new Exception("inner")));
        var activity = new FetchDimensionsActivity(repo.Object);

        await Assert.ThrowsAsync<DimensionScopeDeniedException>(() => activity.RunAsync(new FetchDimensionsInput(UserId, TenantId)));
    }

    [Fact]
    public async Task RunAsync_DictionaryGap_PropagatesUncaught_FailsTheWholeRun()
    {
        var repo = BuildHealthyRepository();
        repo.Setup(r => r.GetRiskAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionDictionaryGapException("Risk", new Exception("inner")));
        var activity = new FetchDimensionsActivity(repo.Object);

        await Assert.ThrowsAsync<DimensionDictionaryGapException>(() => activity.RunAsync(new FetchDimensionsInput(UserId, TenantId)));
    }

    /// <summary>[TRAP this guards] All fifteen failing is total failure, not a fifteen-placeholder "partial" report with nothing real in it.</summary>
    [Fact]
    public async Task RunAsync_AllFifteenDimensionsFail_ThrowsRatherThanReturningAnEmptyReport()
    {
        var repo = new Mock<IDimensionRepository>();
        repo.Setup(r => r.GetLocationAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("Location", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetEntityAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("Entity", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetRiskAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("Risk", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetNatureAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("Nature", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetDepartmentsAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("Departments", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetActAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("Act", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetUsersAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("Users", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetInternalAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("Internal", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetEventAsync(UserId, TenantId, null, 12, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("Event", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetLicenceAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("Licence", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetBacklogAgingAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("BacklogAging", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetTimelinessFYAsync(UserId, TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("TimelinessFY", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetForwardPipelineAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("ForwardPipeline", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetEvidenceIntegrityAsync(UserId, TenantId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("EvidenceIntegrity", TenantId, new Exception("inner")));
        repo.Setup(r => r.GetForwardRiskAsync(UserId, TenantId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DimensionReconciliationException("ForwardRisk", TenantId, new Exception("inner")));

        var activity = new FetchDimensionsActivity(repo.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => activity.RunAsync(new FetchDimensionsInput(UserId, TenantId)));
    }
}
