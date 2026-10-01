/*===========================================================================
  RegTrack Insights - v2 (2026-09-29): follow RegTrack's own Detailed Report
  DICTIONARY VERSION 2 - "overdue" means what RegTrack means

  RegTrack (Kendo_DetailedReport_Pagination) shows a due date as 'Overdue'
  only when its latest status is Open (status 1) and it is due before today.
  Every other not-closed status keeps its workflow name there (Pending For
  Review, Rejected, In Progress, Revise Compliance, Deviation ...).
  Dictionary v1 flagged 17 statuses as overdue-eligible (a BA-style ruling
  that was never actually given by a BA). v2 flags ONLY status 1.

  What changes:
    1. CK_ISC_Coherent required OverdueEligible = 1 <=> ClosureClass = 'open'.
       v2 needs "open but not overdue" (e.g. pending review), so the check is
       relaxed to its real purpose: nothing completed or terminal can ever be
       overdue-eligible. ClosureClass / Timeliness are NOT changed, so every
       open/closed/timeliness metric keeps its meaning.
    2. VersionId 2 = a copy of VersionId 1 (all 23 statuses, all enum/polarity
       rows) with OverdueEligible = 1 only for status 1.
    3. VersionId 2 becomes current (vInsightsStatusCurrent follows IsCurrent).
  VersionId 1 is kept untouched - rollback = make it current again
  (sql/v2/99_rollback_v2.sql).

  ASCII only. Atomic: XACT_ABORT + one transaction (see sql/01's own trap on
  a half-applied DELETE-then-INSERT seed).
===========================================================================*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    /*-- 1. Relax the coherence check to its real purpose ---------------*/
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ISC_Coherent'
                 AND parent_object_id = OBJECT_ID('dbo.InsightsStatusClassification'))
        ALTER TABLE dbo.InsightsStatusClassification DROP CONSTRAINT CK_ISC_Coherent;

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ISC_NoOverdueWhenClosed'
                     AND parent_object_id = OBJECT_ID('dbo.InsightsStatusClassification'))
        ALTER TABLE dbo.InsightsStatusClassification ADD CONSTRAINT CK_ISC_NoOverdueWhenClosed
            CHECK (NOT (OverdueEligible = 1 AND ClosureClass <> 'open'));

    /*-- 2. Version 2 = version 1 with RegTrack's overdue flag -----------*/
    IF NOT EXISTS (SELECT 1 FROM dbo.InsightsDictionaryVersion WHERE VersionId = 2)
        INSERT dbo.InsightsDictionaryVersion (VersionId, VersionLabel, IsCurrent, Notes)
        VALUES (2, '2.0', 0,
                N'RegTrack parity (2026-09-29): only status 1 (Open) is overdue-eligible, matching '
              + N'RegTrack''s Detailed Report. Everything else copied unchanged from v1.');

    DELETE FROM dbo.InsightsStatusClassification WHERE VersionId = 2;
    INSERT dbo.InsightsStatusClassification
        (StatusId, VersionId, RealMeaning, DisplayNameNote, OverdueEligible, ClosureClass, Timeliness, IsDeprecated, Notes)
    SELECT StatusId, 2, RealMeaning, DisplayNameNote,
           CAST(CASE WHEN StatusId = 1 THEN 1 ELSE 0 END AS BIT),
           ClosureClass, Timeliness, IsDeprecated, Notes
    FROM dbo.InsightsStatusClassification
    WHERE VersionId = 1;

    DELETE FROM dbo.InsightsEnumPolarity WHERE VersionId = 2;
    INSERT dbo.InsightsEnumPolarity (VersionId, Semantic, RawValue, Meaning, AppliesTo, Notes)
    SELECT 2, Semantic, RawValue, Meaning, AppliesTo, Notes
    FROM dbo.InsightsEnumPolarity
    WHERE VersionId = 1;

    /*-- Guard: v2 must be a complete copy, or nothing is committed ------*/
    IF (SELECT COUNT(*) FROM dbo.InsightsStatusClassification WHERE VersionId = 2)
       <> (SELECT COUNT(*) FROM dbo.InsightsStatusClassification WHERE VersionId = 1)
       OR (SELECT COUNT(*) FROM dbo.InsightsEnumPolarity WHERE VersionId = 2)
       <> (SELECT COUNT(*) FROM dbo.InsightsEnumPolarity WHERE VersionId = 1)
       OR (SELECT COUNT(*) FROM dbo.InsightsStatusClassification WHERE VersionId = 2 AND OverdueEligible = 1) <> 1
        THROW 51005, N'DICTIONARY v2 - copy incomplete or more than one overdue-eligible status. Rolled back.', 1;

    /*-- 3. Make v2 current ---------------------------------------------*/
    UPDATE dbo.InsightsDictionaryVersion
       SET IsCurrent = CASE WHEN VersionId = 2 THEN 1 ELSE 0 END;

    COMMIT TRANSACTION;
    PRINT 'Dictionary v2 installed and current (only status 1 = Open is overdue-eligible).';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
GO
