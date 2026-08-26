/*===========================================================================
  RegTrack Insights - Phase 1d, build order item 17 (paid_batch keep-warm lane)
  GENERATED REPORT - VIEW TRACKING

  Spec reference : design doc Sec.4.3 ("Keep-warm, not generate-all")

  -- WHY THIS EXISTS -------------------------------------------------------
  Sec.4.3: keep-warm may only re-run a (scope, type, period) key that has
  been generated at least once AND VIEWED RECENTLY (60-90 day window).
  Nothing recorded "viewed" before this script - GeneratedReport tracked
  only generation, never access. Without this column, a keep-warm scheduler
  can only ask "was this ever generated", which is precisely the anti-pattern
  Sec.4.3 names by name: "generating every report-type x every entity-scope
  for every tenant is not 600 runs - it is thousands, most of which nobody
  opens."

  -- ADDITIVE. Adds one nullable column to an existing table. Touches
     nothing else. No rollback entry needed - sql/99's existing
     DROP TABLE dbo.GeneratedReport already removes this column with it.
===========================================================================*/

SET NOCOUNT ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.GeneratedReport') AND name = 'LastViewedUtc'
)
BEGIN
    ALTER TABLE dbo.GeneratedReport ADD LastViewedUtc DATETIME2(0) NULL;
END
GO

PRINT 'GeneratedReport.LastViewedUtc installed.';
GO
