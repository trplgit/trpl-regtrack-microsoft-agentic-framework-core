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

  -- ADDITIVE. Adds one nullable column and one CHECK constraint to an
     existing table. Touches nothing else. No error block reserved here -
     nothing THROWs in this file, same reasoning as sql/18 and sql/19.

  -- [ADDED 2026-09-18, reconciled from Vinay's handoff] THE DIMENSION IS
     ALREADY IN THE KEY, BY A DIFFERENT ROUTE.

     Every dimension_selection row in production carries the dimension as a
     suffix on Period itself: '90day::dim=act', '90day::dim=departments', and
     so on. Some UAT rows instead show hand-mangled Periods like
     'FY2025-26-dimtest-act' - someone editing the Period string to dodge a
     collision, one test run at a time.

     This column is the proper fix for that. But once it exists there are TWO
     places recording the same fact, and nothing forces them to agree: a row
     could read Period '90day::dim=act' with RequestedDimensions 'nature' and
     no reader would notice. The CHECK below closes that gap. It does not
     decide which route is authoritative - that is a .NET decision. If the
     writer stops emitting '::dim=' once this column is populated, the
     constraint keeps passing (no suffix means nothing to disagree with).
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

/*---------------------------------------------------------------------------
  AGREEMENT CONSTRAINT

  Rejects a row where Period names one dimension and RequestedDimensions names
  a different one. Deliberately permissive in three places, because a CHECK
  that blocks a legitimate write is worse than the disagreement it prevents:

    1. RequestedDimensions IS NULL always passes - NULL means "all dimensions"
       and is what every pre-existing production row holds.
    2. A Period with no '::dim=' always passes - fixed_holistic has nothing to
       disagree with, and so will dimension_selection once the .NET writer
       drops the suffix in favor of this column.
    3. RequestedDimensions may be a comma-separated LIST - the test is
       MEMBERSHIP, not equality: 'act,nature' agrees with '::dim=act'.

  Comma-delimiting both sides stops the substring trap - 'actuarial' does NOT
  satisfy '::dim=act', because ',actuarial,' does not contain ',act,'.
---------------------------------------------------------------------------*/
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID('dbo.GeneratedReport')
      AND name = 'CK_GeneratedReport_DimensionKeyAgrees'
)
BEGIN
    ALTER TABLE dbo.GeneratedReport WITH CHECK
    ADD CONSTRAINT CK_GeneratedReport_DimensionKeyAgrees CHECK (
            RequestedDimensions IS NULL
         OR CHARINDEX('::dim=', Period) = 0
         OR ',' + REPLACE(RequestedDimensions, ' ', '') + ','
            LIKE '%,' + SUBSTRING(Period, CHARINDEX('::dim=', Period) + 6, 200) + ',%'
    );
END
GO

PRINT 'GeneratedReport.RequestedDimensions installed.';
GO
