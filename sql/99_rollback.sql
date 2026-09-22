/*===========================================================================
  RegTrack Insights - ROLLBACK
  Cleanly removes every object created by sql/01 - sql/41.
  (sql/42 is a validation script and creates nothing permanent.)

  SAFETY: this script touches ONLY objects created by the Insights scripts.
          It does NOT reference, alter, or delete any existing RegTrack table
          or business data. Verify before running:
              SELECT name, type_desc FROM sys.objects
              WHERE name LIKE '%Insights%' ORDER BY type_desc, name;

  ORDER: procedures/views first, then functions, then tables (FK dependency:
         InsightsStatusClassification and InsightsEnumPolarity both reference
         InsightsDictionaryVersion).

  USE WHEN: a UAT install went wrong, or you are re-installing from scratch.
  DO NOT run in production once reports have been generated - dropping the
  dictionary tables invalidates the provenance of every stored report.

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
===========================================================================*/

SET NOCOUNT ON;
GO

PRINT 'Rolling back RegTrack Insights objects...';
GO

/*-- 1. Stored procedures ---------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_Dimension_Location',   'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Location;
IF OBJECT_ID('dbo.usp_Insights_FreeDigestAggregates', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestAggregates;
IF OBJECT_ID('dbo.usp_Insights_FreeDigestGate',       'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestGate;
IF OBJECT_ID('dbo.usp_Insights_EvaluateGate',         'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_EvaluateGate;
IF OBJECT_ID('dbo.usp_Insights_TenantShape',          'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_TenantShape;
IF OBJECT_ID('dbo.usp_Insights_EntityRollup',         'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_EntityRollup;
IF OBJECT_ID('dbo.usp_Insights_FindScopelessUsers',   'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FindScopelessUsers;
IF OBJECT_ID('dbo.usp_Insights_AuditScope',           'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_AuditScope;
IF OBJECT_ID('dbo.usp_Insights_ClassifyScope',        'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_ClassifyScope;
IF OBJECT_ID('dbo.usp_Insights_GoldenInvariants',     'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_GoldenInvariants;
IF OBJECT_ID('dbo.usp_Insights_StatusDataQuality',    'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_StatusDataQuality;
IF OBJECT_ID('dbo.usp_Insights_AssertStatusCoverage', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_AssertStatusCoverage;
GO

/*   Dimension procedures (sql/05, 07-14, 21-27)  */
IF OBJECT_ID('dbo.usp_Insights_Dimension_Licence',         'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Licence;
IF OBJECT_ID('dbo.usp_Insights_Dimension_CoverageGaps',      'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_CoverageGaps;
IF OBJECT_ID('dbo.usp_Insights_Dimension_ForwardRisk',       'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_ForwardRisk;
IF OBJECT_ID('dbo.usp_Insights_Dimension_EvidenceIntegrity', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_EvidenceIntegrity;
IF OBJECT_ID('dbo.usp_Insights_Dimension_ForwardPipeline',   'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_ForwardPipeline;
IF OBJECT_ID('dbo.usp_Insights_Dimension_TimelinessFY',      'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_TimelinessFY;
IF OBJECT_ID('dbo.usp_Insights_Dimension_BacklogAging',      'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_BacklogAging;
IF OBJECT_ID('dbo.usp_Insights_Dimension_Users',       'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Users;
IF OBJECT_ID('dbo.usp_Insights_Dimension_Internal',    'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Internal;
IF OBJECT_ID('dbo.usp_Insights_Dimension_Event',       'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Event;
IF OBJECT_ID('dbo.usp_Insights_Dimension_Act',         'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Act;
IF OBJECT_ID('dbo.usp_Insights_Dimension_Departments', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Departments;
IF OBJECT_ID('dbo.usp_Insights_Dimension_Nature',      'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Nature;
IF OBJECT_ID('dbo.usp_Insights_Dimension_Risk',        'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Risk;
IF OBJECT_ID('dbo.usp_Insights_Dimension_Entity',      'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_Dimension_Entity;
GO

/*   Free-tier send log + suppression procedures (sql/15, 16)  */
IF OBJECT_ID('dbo.usp_Insights_FreeDigestReleaseClaim',  'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestReleaseClaim;
IF OBJECT_ID('dbo.usp_Insights_FreeDigestRecordOutcome', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestRecordOutcome;
IF OBJECT_ID('dbo.usp_Insights_FreeDigestClaimSend',     'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestClaimSend;
IF OBJECT_ID('dbo.usp_Insights_DigestSuppressionList',   'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_DigestSuppressionList;
IF OBJECT_ID('dbo.usp_Insights_DigestUnsuppress',        'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_DigestUnsuppress;
IF OBJECT_ID('dbo.usp_Insights_DigestSuppress',          'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_DigestSuppress;
IF OBJECT_ID('dbo.usp_Insights_EligibleTenants',         'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_EligibleTenants;
/*  sql/33 alters an EXISTING, live table (GeneratedReport, sql/18) rather than
    creating one. Rollback therefore drops the constraint and the column, never
    the table - GeneratedReport carries the paid pipeline's production history. */
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE parent_object_id = OBJECT_ID('dbo.GeneratedReport')
             AND name = 'CK_GeneratedReport_DimensionKeyAgrees')
    ALTER TABLE dbo.GeneratedReport DROP CONSTRAINT CK_GeneratedReport_DimensionKeyAgrees;
IF COL_LENGTH('dbo.GeneratedReport', 'RequestedDimensions') IS NOT NULL
    ALTER TABLE dbo.GeneratedReport DROP COLUMN RequestedDimensions;

IF OBJECT_ID('dbo.usp_Insights_AgentReasoningSize',      'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_AgentReasoningSize;
IF OBJECT_ID('dbo.usp_Insights_AgentReasoningPurge',     'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_AgentReasoningPurge;
IF OBJECT_ID('dbo.usp_Insights_InsightJsonReleaseClaim',  'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_InsightJsonReleaseClaim;
IF OBJECT_ID('dbo.usp_Insights_InsightJsonRecordOutcome', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_InsightJsonRecordOutcome;
IF OBJECT_ID('dbo.usp_Insights_InsightJsonClaimPost',     'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_InsightJsonClaimPost;
IF OBJECT_ID('dbo.usp_Insights_SnapshotPurge',           'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_SnapshotPurge;
IF OBJECT_ID('dbo.usp_Insights_SnapshotTrend',           'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_SnapshotTrend;
IF OBJECT_ID('dbo.usp_Insights_SnapshotRecord',          'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_SnapshotRecord;
GO

/*   Free-tier digest artifact procedures (sql/29). [RESTORED 2026-09-18] These
     were present in the committed rollback and missing from the working copy,
     while sql/29 still creates them - the verification step below would have
     reported the rollback INCOMPLETE.                                         */
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactDelete',         'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactDelete;
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactsForPurge',      'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactsForPurge;
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactMarkDispatched', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactMarkDispatched;
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactsForDispatch',   'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactsForDispatch;
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactRelease',        'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactRelease;
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactComplete',       'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactComplete;
IF OBJECT_ID('dbo.usp_Insights_FreeDigestArtifactClaim',          'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeDigestArtifactClaim;
GO

/*   Free-tier MONTHLY edition (sql/34 - sql/41). Callers first, then the
     helpers they call. To remove ONLY the monthly tier, run just this
     batch (the eight DROP lines below, up to the next GO) - nothing else
     in this file depends on them.                                            */
IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Overview',        'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Overview;
IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Users',           'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Users;
IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Location',        'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Location;
IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Act',             'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Act;
IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_Licence',         'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_Licence;
IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_MemberDetectors', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_MemberDetectors;
IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_LoadLicences',    'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_LoadLicences;
IF OBJECT_ID('dbo.usp_Insights_FreeMonthly_LoadFacts',       'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_FreeMonthly_LoadFacts;
GO

/*   Metric snapshot procedures (sql/33)  */
IF OBJECT_ID('dbo.usp_Insights_SnapshotPurge',  'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_SnapshotPurge;
IF OBJECT_ID('dbo.usp_Insights_SnapshotTrend',  'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_SnapshotTrend;
IF OBJECT_ID('dbo.usp_Insights_SnapshotRecord', 'P') IS NOT NULL DROP PROCEDURE dbo.usp_Insights_SnapshotRecord;
GO

/*   sql/28 alters an EXISTING, live table (GeneratedReport, sql/18) rather than
     creating one. Rollback therefore drops the constraint and the column, never
     the table - GeneratedReport carries the paid pipeline's production history. */
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE parent_object_id = OBJECT_ID('dbo.GeneratedReport')
             AND name = 'CK_GeneratedReport_DimensionKeyAgrees')
    ALTER TABLE dbo.GeneratedReport DROP CONSTRAINT CK_GeneratedReport_DimensionKeyAgrees;
IF COL_LENGTH('dbo.GeneratedReport', 'RequestedDimensions') IS NOT NULL
    ALTER TABLE dbo.GeneratedReport DROP COLUMN RequestedDimensions;
GO

/*-- 2. Functions (dropped after the procs that call them) ------------------*/
IF OBJECT_ID('dbo.tvfInsightsScopedInstances',   'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsScopedInstances;
IF OBJECT_ID('dbo.tvfInsightsScopePairs',        'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsScopePairs;
IF OBJECT_ID('dbo.tvfInsightsEntityTree',        'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsEntityTree;
IF OBJECT_ID('dbo.tvfInsightsOverdueSchedules',  'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsOverdueSchedules;
IF OBJECT_ID('dbo.tvfInsightsLatestStatus',      'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsLatestStatus;
IF OBJECT_ID('dbo.tvfInsightsOwnership',         'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsOwnership;
IF OBJECT_ID('dbo.tvfInsightsManagementUsers',   'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsManagementUsers;
IF OBJECT_ID('dbo.tvfInsightsForwardPipelineSchedules', 'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsForwardPipelineSchedules;
GO

/*-- 3. View ----------------------------------------------------------------*/
IF OBJECT_ID('dbo.vInsightsStatusCurrent', 'V') IS NOT NULL DROP VIEW dbo.vInsightsStatusCurrent;
GO

/*-- 4. Tables (children before parent - FK to InsightsDictionaryVersion) ---*/
/*   Free-tier tables (sql/15, 16). No FKs, so order is irrelevant.
     NOTE: dropping InsightsFreeDigestLog discards the weekly-once guarantee.
     Re-running a digest after a rollback can re-send email to real recipients. */
/*  Operational / diagnostic tables that are NOT part of the product but DO
    match the '%Insights%' pattern the verification step below uses. Left in
    place they make the rollback report itself incomplete.                   */
IF OBJECT_ID('dbo.InsightsObjectBackup_20260904','U') IS NOT NULL DROP TABLE dbo.InsightsObjectBackup_20260904;  -- pre-deployment definition snapshot, UAT only
IF OBJECT_ID('dbo.InsightsTenantTokenUsage',  'U') IS NOT NULL DROP TABLE dbo.InsightsTenantTokenUsage;   -- created by the .NET layer's cost instrumentation; DDL not in this repo
IF OBJECT_ID('dbo.InsightsFreeDigestLog',     'U') IS NOT NULL DROP TABLE dbo.InsightsFreeDigestLog;
IF OBJECT_ID('dbo.InsightsDigestSuppression', 'U') IS NOT NULL DROP TABLE dbo.InsightsDigestSuppression;
/*  sql/28 - NOTE: dropping InsightsMetricSnapshot destroys ALL trend history
    and it cannot be backfilled - the source data holds only the present.
    Think before running this in an environment that has been accumulating. */
/*  Post-log (sql/31) and reasoning capture (sql/32). Both APPEND-ONLY.
    InsightsAgentReasoningLog has a 90-day purge (sql/32); the snapshot table's
    floor is 400 days because it serves trends, not incidents.
    Dropping it discards the only permanent record of what each agent said it
    was doing on each run.                                                    */
/*  [RESTORED 2026-09-18] sql/29 and sql/30 tables - present in the committed
    rollback, missing from the working copy, still created by those scripts.
    sql/29 NOTE: dropping this table discards every artifact index row.
    Delete the corresponding "insights-digests" blob container contents
    separately - this script only ever touches SQL, never blob storage.     */
IF OBJECT_ID('dbo.InsightsFreeDigestArtifact', 'U') IS NOT NULL DROP TABLE dbo.InsightsFreeDigestArtifact;
IF OBJECT_ID('dbo.InsightsReportRequest',      'U') IS NOT NULL DROP TABLE dbo.InsightsReportRequest;   -- sql/30, fan-out reqId -> runId grouping
/*  sql/31 - NOTE: dropping this table discards the insight JSON's weekly-once
    guarantee. Re-running the lane after a rollback can re-POST a recipient's
    insight for a week already posted.                                     */
IF OBJECT_ID('dbo.InsightsFreeDigestJsonLog',  'U') IS NOT NULL DROP TABLE dbo.InsightsFreeDigestJsonLog;
IF OBJECT_ID('dbo.InsightsAgentReasoningLog',  'U') IS NOT NULL DROP TABLE dbo.InsightsAgentReasoningLog;
IF OBJECT_ID('dbo.InsightsMetricSnapshot',     'U') IS NOT NULL DROP TABLE dbo.InsightsMetricSnapshot;

IF OBJECT_ID('dbo.InsightsStatusClassification', 'U') IS NOT NULL DROP TABLE dbo.InsightsStatusClassification;
IF OBJECT_ID('dbo.InsightsEnumPolarity',         'U') IS NOT NULL DROP TABLE dbo.InsightsEnumPolarity;
IF OBJECT_ID('dbo.InsightsDictionaryVersion',    'U') IS NOT NULL DROP TABLE dbo.InsightsDictionaryVersion;
GO

/*-- 5. Verify nothing remains ----------------------------------------------*/
DECLARE @remaining INT = (
    SELECT COUNT(*) FROM sys.objects
    WHERE (name LIKE '%Insights%' OR name LIKE 'tvfInsights%')
      AND type IN ('P','V','U','IF','FN','TF'));

IF @remaining > 0
BEGIN
    SELECT name, type_desc FROM sys.objects
    WHERE (name LIKE '%Insights%' OR name LIKE 'tvfInsights%')
      AND type IN ('P','V','U','IF','FN','TF');
    RAISERROR (N'ROLLBACK INCOMPLETE - objects listed above still exist. Drop manually.', 16, 1);
END
ELSE
    PRINT 'Rollback complete - all Insights objects removed.';
GO
