using System.Text.Json;
using Insights.Domain;
using Insights.Worker.Orchestration;
using Insights.Worker.Orchestration.Activities;

namespace Insights.UnitTests;

/// <summary>
/// [ADDED 2026-09-15] Real end-to-end wiring check for the timing-line gate check added the same
/// day - three real orchestrator runs (Life Cell/Minda/Agrocel) all reached Completed with the
/// info line still missing, even though real rows with real completed-event data (100%/99%+
/// OnTimePct) existed. The gate's own pure logic tested fine in isolation
/// (UserDimensionStructureGateTests), and a standalone Newtonsoft round-trip of the input record
/// also preserved UsersRowsJson correctly - so this closes the remaining gap: does the ACTIVITY
/// itself, given the REAL UsersRow type serialized the SAME way FetchDimensionsActivity/the
/// orchestrator actually do it (System.Text.Json, then JsonDocument "Rows" extraction), correctly
/// detect a qualifying row and refuse?
/// </summary>
public sealed class ValidateUserDimensionStructureActivityTests
{
    private static string BuildUsersRowsJson(params UsersRow[] rows)
    {
        // Mirrors InsightsReportOrchestrator.cs's own dimensionRowsJson extraction EXACTLY:
        // FetchDimensionsActivity serializes the whole DimensionResult, the orchestrator pulls
        // just the "Rows" property back out as raw text via JsonDocument.
        var wrapped = new { Rows = rows };
        var fullJson = JsonSerializer.Serialize(wrapped);
        return JsonDocument.Parse(fullJson).RootElement.GetProperty("Rows").GetRawText();
    }

    private const string WellFormedHtmlWithoutTimingLine = """
        <div class="di-tabsroot">
          <input type="radio" name="di-tab" id="di-tab-1" class="di-tab-input" checked>
          <input type="radio" name="di-tab" id="di-tab-2" class="di-tab-input">
          <input type="radio" name="di-tab" id="di-tab-3" class="di-tab-input">
          <input type="radio" name="di-tab" id="di-tab-4" class="di-tab-input">
          <div class="di-stickytabs">
            <nav class="di-tabnav">
              <label for="di-tab-1" class="di-tab">Overview</label>
              <label for="di-tab-2" class="di-tab">Priority load</label>
              <label for="di-tab-3" class="di-tab">Standouts</label>
              <label for="di-tab-4" class="di-tab">What this means</label>
            </nav>
          </div>
          <section class="di-pane" id="di-pane-1"></section>
          <section class="di-pane" id="di-pane-2"></section>
          <section class="di-pane" id="di-pane-3"></section>
          <section class="di-pane" id="di-pane-4"></section>
        </div>
        <div class="di-donut ur-donut" aria-hidden="true">
          <svg viewBox="0 0 120 120">
            <circle class="di-donut__track" cx="60" cy="60" r="52"></circle>
            <circle class="di-donut__arc" cx="60" cy="60" r="52"></circle>
          </svg>
        </div>
        <div class="ur-rolesgrid">
          <div class="ur-role ur-role--perf"><div class="ur-role__name">Performer</div></div>
        </div>
        <div class="ur-lensroot">
          <input type="radio" name="ur-lens" id="ur-lens-1" class="ur-lens-radio" checked>
          <input type="radio" name="ur-lens" id="ur-lens-2" class="ur-lens-radio">
          <div class="ur-pulist ur-pulist-1"></div>
          <div class="ur-pulist ur-pulist-2"></div>
        </div>
        """;

    [Fact]
    public async Task RunAsync_Throws_WhenRealUsersRowHasQualifyingTimingSampleAndHtmlLacksTimingLine()
    {
        var qualifyingRow = new UsersRow { UserID = 12116, MedianDaysEarlyLate = 12.0m, TimingSampleSize = 486 };
        var rowsJson = BuildUsersRowsJson(qualifyingRow);

        var activity = new ValidateUserDimensionStructureActivity();
        var input = new ValidateUserDimensionStructureInput(
            WellFormedHtmlWithoutTimingLine, DimensionSelectionComposition.ReportType, ["Users"], rowsJson);

        var ex = await Assert.ThrowsAsync<OrchestrationRefusedException>(() => activity.RunAsync(input));
        Assert.Contains(ex.InternalDiagnostics, v => v.Contains("ur-timingline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_Approves_WhenNoRowHasQualifyingTimingSample()
    {
        var thinRow = new UsersRow { UserID = 22424, MedianDaysEarlyLate = null, TimingSampleSize = 3 };
        var rowsJson = BuildUsersRowsJson(thinRow);

        var activity = new ValidateUserDimensionStructureActivity();
        var input = new ValidateUserDimensionStructureInput(
            WellFormedHtmlWithoutTimingLine, DimensionSelectionComposition.ReportType, ["Users"], rowsJson);

        var result = await activity.RunAsync(input);
        Assert.Equal(WellFormedHtmlWithoutTimingLine, result.Html);
    }
}
