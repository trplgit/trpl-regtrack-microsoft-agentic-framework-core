/*===========================================================================
  RegTrack Insights - dimension_selection cooldown fix (ADR-021, 2026-09-09)
  GENERATED REPORT - REQUESTED DIMENSIONS

  Spec reference : design doc Sec.2.4, AMENDED - the 30-day cooldown key was
  (scope, report-type, period). That predates the "dimension_selection" report
  type (a caller picks ONE of fourteen dimensions and gets a report scoped to
  just that one). Without this column, GeneratedReport has no way to record
  WHICH dimension a completed run covered, so the cooldown check
  (EfCooldownRepository.CheckAsync) could not tell a completed Nature report
  from a completed Entity report - generating Nature incorrectly locked
  Entity, Departments, every other dimension, for the same tenant/period.

  The key is now (scope, report-type, period, requested-dimensions). NULL
  here means "all dimensions" (fixed_holistic, and every report type other
  than dimension_selection) - matching the null-means-all convention
  RequestedDimensions already carries everywhere else in this codebase
  (InsightsReportOrchestrationInput, FetchDimensionsInput, etc).

  -- REQUIRES sql/18. If dbo.GeneratedReport does not exist yet, the ALTER
     below fails loudly with error 4902 - that is intended, not a bug in the
     guard: the sys.columns lookup returns no rows when the table itself is
     absent, so "column not found" and "table not found" both take the same
     ALTER branch, and only the table-not-found case throws. Do NOT rewrite
     this as `IF OBJECT_ID(...) IS NOT NULL AND NOT EXISTS (...)` - that
     would silently no-op instead of failing when the table is missing.

  -- ADDITIVE. Adds one nullable column to an existing table. Touches
     nothing else. No error block reserved here - nothing THROWs in this
     file, same reasoning as sql/18 and sql/19's own install scripts.
===========================================================================*/

SET NOCOUNT ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.GeneratedReport') AND name = 'RequestedDimensions'
)
BEGIN
    ALTER TABLE dbo.GeneratedReport ADD RequestedDimensions NVARCHAR(200) NULL;
END
GO

PRINT 'GeneratedReport.RequestedDimensions installed.';
GO
