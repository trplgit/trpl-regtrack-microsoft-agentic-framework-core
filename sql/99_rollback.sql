/*═══════════════════════════════════════════════════════════════════════════
  RegTrack Insights — ROLLBACK
  Cleanly removes every object created by sql/01 – sql/06.

  SAFETY: this script touches ONLY objects created by the Insights scripts.
          It does NOT reference, alter, or delete any existing RegTrack table
          or business data. Verify before running:
              SELECT name, type_desc FROM sys.objects
              WHERE name LIKE '%Insights%' ORDER BY type_desc, name;

  ORDER: procedures/views first, then functions, then tables (FK dependency:
         InsightsStatusClassification and InsightsEnumPolarity both reference
         InsightsDictionaryVersion).

  USE WHEN: a UAT install went wrong, or you are re-installing from scratch.
  DO NOT run in production once reports have been generated — dropping the
  dictionary tables invalidates the provenance of every stored report.

  IDEMPOTENT. Target: SQL Server (vitComplianceSystem)
═══════════════════════════════════════════════════════════════════════════*/

SET NOCOUNT ON;
GO

PRINT 'Rolling back RegTrack Insights objects...';
GO

/*── 1. Stored procedures ───────────────────────────────────────────────────*/
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

/*── 2. Functions (dropped after the procs that call them) ──────────────────*/
IF OBJECT_ID('dbo.tvfInsightsScopedInstances',   'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsScopedInstances;
IF OBJECT_ID('dbo.tvfInsightsScopePairs',        'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsScopePairs;
IF OBJECT_ID('dbo.tvfInsightsEntityTree',        'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsEntityTree;
IF OBJECT_ID('dbo.tvfInsightsOverdueSchedules',  'IF') IS NOT NULL DROP FUNCTION dbo.tvfInsightsOverdueSchedules;
GO

/*── 3. View ────────────────────────────────────────────────────────────────*/
IF OBJECT_ID('dbo.vInsightsStatusCurrent', 'V') IS NOT NULL DROP VIEW dbo.vInsightsStatusCurrent;
GO

/*── 4. Tables (children before parent — FK to InsightsDictionaryVersion) ───*/
IF OBJECT_ID('dbo.InsightsStatusClassification', 'U') IS NOT NULL DROP TABLE dbo.InsightsStatusClassification;
IF OBJECT_ID('dbo.InsightsEnumPolarity',         'U') IS NOT NULL DROP TABLE dbo.InsightsEnumPolarity;
IF OBJECT_ID('dbo.InsightsDictionaryVersion',    'U') IS NOT NULL DROP TABLE dbo.InsightsDictionaryVersion;
GO

/*── 5. Verify nothing remains ──────────────────────────────────────────────*/
DECLARE @remaining INT = (
    SELECT COUNT(*) FROM sys.objects
    WHERE (name LIKE '%Insights%' OR name LIKE 'tvfInsights%')
      AND type IN ('P','V','U','IF','FN','TF'));

IF @remaining > 0
BEGIN
    SELECT name, type_desc FROM sys.objects
    WHERE (name LIKE '%Insights%' OR name LIKE 'tvfInsights%')
      AND type IN ('P','V','U','IF','FN','TF');
    RAISERROR (N'ROLLBACK INCOMPLETE — objects listed above still exist. Drop manually.', 16, 1);
END
ELSE
    PRINT 'Rollback complete — all Insights objects removed.';
GO
