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

  -- [RENUMBERED 2026-09-13] Was sql/28, which collided with
     28_metric_snapshot.sql - already deployed on both databases. Two files
     sharing a number in a sequenced deployment resolve by alphabetical luck,
     not by design. This is 33.

  -- [TRAP - VERIFIED IN PRODUCTION 2026-09-13] THE DIMENSION IS ALREADY IN
     THE KEY, BY A DIFFERENT ROUTE.

     All 15 dimension_selection rows in production carry the dimension as a
     suffix on Period itself: '90day::dim=act', '90day::dim=departments',
     '90day::dim=licence', and so on. 15 of 15, no exceptions. All 10
     fixed_holistic rows carry no suffix, also no exceptions.

     So the three-part key ALREADY separates act from departments in
     production today. UAT shows how that came about - 1 of 12 rows has the
     suffix, and the rest carry hand-mangled Periods like
     'FY2025-26-dimtest-act', 'FY2025-26-brandfix-dept'. Someone was editing
     the Period string to dodge the collision, one test run at a time.

     This column is the proper fix for that. But once it exists there are TWO
     places recording the same fact, and nothing forces them to agree: a row
     could read Period '90day::dim=act' with RequestedDimensions 'nature' and
     no reader would notice.

     The CHECK below closes that. It does NOT decide which route is
     authoritative - that is a .NET decision. If the column is the proper fix
     and the suffix was the stopgap, the writer should stop emitting '::dim='
     once it starts populating this column, and the constraint will keep
     passing (no suffix means nothing to disagree with).

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

    1. RequestedDimensions IS NULL always passes. NULL means "all dimensions"
       and is also what all 25 existing production rows hold - the constraint
       must not invalidate history it was added after.

    2. A Period with no '::dim=' always passes. fixed_holistic has nothing to
       disagree with, and so will dimension_selection if the .NET writer drops
       the suffix once this column is authoritative.

    3. RequestedDimensions may be a comma-separated LIST - the column is named
       plural and the null-means-all convention implies non-null may be many.
       The Period suffix carries one dimension, so the test is MEMBERSHIP, not
       equality: 'act,nature' agrees with '::dim=act'.

  Comma-delimiting both sides is what stops the substring trap - 'actuarial'
  does NOT satisfy '::dim=act', because ',actuarial,' does not contain ',act,'.

  Verified 2026-09-13 against ten cases including every real production Period
  value: legacy NULLs pass, genuine disagreement rejects, lists containing the
  dimension pass, lists omitting it reject, and the substring trap rejects.

  WITH CHECK is deliberate - it validates the existing rows now rather than
  trusting them. All 25 hold NULL, so all 25 pass clause 1.
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
