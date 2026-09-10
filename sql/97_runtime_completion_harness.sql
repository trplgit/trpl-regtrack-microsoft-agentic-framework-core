/*===========================================================================
  RegTrack Insights - RUNTIME COMPLETION HARNESS

  Answers the ONE question a marker scan cannot: does every dimension run to
  COMPLETION, or does it die partway and leave result sets un-emitted?

  -- WHY THIS EXISTS ------------------------------------------------------
  Each dimension returns SIX result sets, in order:

      1 control_totals   the reconciliation
      2 rows             per-branch / per-category detail
      3 detector_policy  which detectors fired, and in which mode
      4 assertions       typed facts the narrative layer may use
      5 findings         the headline statements
      6 data_quality     declared caveats

  A client that reads only the FIRST result set will show a perfect
  `control_totals` and report success even when a later statement fails.
  That is not hypothetical:

    * usp_Insights_Dimension_Location raised "Ambiguous column name
      'VsPeerStateNormPP'" at the ASSERTIONS step. Sets 1-3 emitted, then it
      died. Sets 4, 5 and 6 never appeared - and every check that read only
      set 1 reported PASS.

    * The same blind spot hid a detector reporting 120 flagged of 99 eligible
      (121.2%), which lives in set 3.

  -- HOW IT WORKS ---------------------------------------------------------
  Every SELECT that emits a result set is unconditional, so a procedure that
  finishes without error HAS emitted all six. TRY/CATCH therefore answers the
  question exactly: completion == six result sets.

  The INSERT after each EXEC runs only if the EXEC completed. Verified on UAT
  2026-09-08: the CATCH path captured a real THROW (51030) with line number,
  and the completion path recorded only after all six sets had emitted.

  -- HOW TO RUN -----------------------------------------------------------
  SSMS only. Set the two variables below to a user with real scope on the
  tenant, then execute the whole file as ONE batch (do not add GO).

  You will see a lot of result grids - roughly 16 x 6. IGNORE THEM.
  The LAST grid is the summary. That is the only one that matters.

  Read-only: this script creates no permanent objects and changes no data.
===========================================================================*/

SET NOCOUNT ON;

/*-- SET THESE TWO ---------------------------------------------------------
    UAT   : @UserID = 38,    @CustomerID = 29
    PROD  : @UserID = 34280, @CustomerID = 1216   (Trent)
            @UserID = 52866, @CustomerID = 1817   (Gargi - smaller, faster)   */
DECLARE @UserID     INT = 38;
DECLARE @CustomerID INT = 29;

IF OBJECT_ID('tempdb..#result') IS NOT NULL DROP TABLE #result;
CREATE TABLE #result (
    Seq       INT IDENTITY(1,1),
    Dimension SYSNAME,
    Outcome   VARCHAR(40),
    ErrNum    INT NULL,
    ErrLine   INT NULL,
    ErrMsg    NVARCHAR(400) NULL);

/*-- Pre-flight: is the scope even valid? Every dimension THROWs 51030 without
    it, which would look like 16 failures rather than one bad parameter.     */
IF NOT EXISTS (SELECT 1 FROM dbo.tvfInsightsScopePairs(@UserID, @CustomerID))
BEGIN
    SELECT 'STOP - user has no scope on this tenant. Every dimension will THROW 51030.' AS Problem,
           @UserID AS UserID, @CustomerID AS CustomerID;
    RETURN;
END

BEGIN TRY EXEC dbo.usp_Insights_Dimension_Location        @UserID, @CustomerID;
    INSERT #result VALUES ('Location','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('Location','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_Entity          @UserID, @CustomerID;
    INSERT #result VALUES ('Entity','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('Entity','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_Risk            @UserID, @CustomerID;
    INSERT #result VALUES ('Risk','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('Risk','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_Nature          @UserID, @CustomerID;
    INSERT #result VALUES ('Nature','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('Nature','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_Departments     @UserID, @CustomerID;
    INSERT #result VALUES ('Departments','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('Departments','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_Act             @UserID, @CustomerID;
    INSERT #result VALUES ('Act','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('Act','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_Users           @UserID, @CustomerID;
    INSERT #result VALUES ('Users','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('Users','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_Internal        @UserID, @CustomerID;
    INSERT #result VALUES ('Internal','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('Internal','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_Event           @UserID, @CustomerID;
    INSERT #result VALUES ('Event','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('Event','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_Licence         @UserID, @CustomerID;
    INSERT #result VALUES ('Licence','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('Licence','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_BacklogAging    @UserID, @CustomerID;
    INSERT #result VALUES ('BacklogAging','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('BacklogAging','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_TimelinessFY    @UserID, @CustomerID;
    INSERT #result VALUES ('TimelinessFY','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('TimelinessFY','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_ForwardPipeline @UserID, @CustomerID;
    INSERT #result VALUES ('ForwardPipeline','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('ForwardPipeline','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_EvidenceIntegrity @UserID, @CustomerID;
    INSERT #result VALUES ('EvidenceIntegrity','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('EvidenceIntegrity','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_ForwardRisk     @UserID, @CustomerID;
    INSERT #result VALUES ('ForwardRisk','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('ForwardRisk','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_Dimension_CoverageGaps    @UserID, @CustomerID;
    INSERT #result VALUES ('CoverageGaps','COMPLETE - 6 result sets',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('CoverageGaps','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

/*-- Supporting procedures. Different signatures, so run separately. -------*/
BEGIN TRY EXEC dbo.usp_Insights_GoldenInvariants  @CustomerID;
    INSERT #result VALUES ('GoldenInvariants','COMPLETE',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('GoldenInvariants','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_StatusDataQuality @CustomerID;
    INSERT #result VALUES ('StatusDataQuality','COMPLETE',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('StatusDataQuality','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_TenantShape       @CustomerID;
    INSERT #result VALUES ('TenantShape','COMPLETE',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('TenantShape','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_ClassifyScope     @UserID, @CustomerID;
    INSERT #result VALUES ('ClassifyScope','COMPLETE',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('ClassifyScope','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_AuditScope        @UserID, @CustomerID;
    INSERT #result VALUES ('AuditScope','COMPLETE',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('AuditScope','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_EligibleTenants   @UserID;
    INSERT #result VALUES ('EligibleTenants','COMPLETE',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('EligibleTenants','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_FreeDigestGate    @CustomerID;
    INSERT #result VALUES ('FreeDigestGate','COMPLETE',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('FreeDigestGate','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

BEGIN TRY EXEC dbo.usp_Insights_FreeDigestAggregates @CustomerID, @UserID;
    INSERT #result VALUES ('FreeDigestAggregates','COMPLETE',NULL,NULL,NULL); END TRY
BEGIN CATCH INSERT #result VALUES ('FreeDigestAggregates','*** FAILED ***',ERROR_NUMBER(),ERROR_LINE(),LEFT(ERROR_MESSAGE(),400)); END CATCH

/*===========================================================================
  THE ONLY GRID THAT MATTERS - the last one.
  Failures sort to the top. An empty failure list is the pass condition.
===========================================================================*/
SELECT
    Dimension,
    Outcome,
    ErrNum,
    ErrLine,
    ErrMsg
FROM #result
ORDER BY CASE WHEN Outcome LIKE '***%' THEN 0 ELSE 1 END, Seq;

DECLARE @failed INT = (SELECT COUNT(*) FROM #result WHERE Outcome LIKE '***%');
DECLARE @total  INT = (SELECT COUNT(*) FROM #result);

IF @failed > 0
    RAISERROR (N'%d of %d objects did NOT run to completion. Any dimension listed as FAILED emitted only SOME of its six result sets - the missing ones include findings and data_quality.', 16, 1, @failed, @total);
ELSE
    PRINT CONCAT('All ', @total, ' objects ran to completion. Every dimension emitted all six result sets.');
