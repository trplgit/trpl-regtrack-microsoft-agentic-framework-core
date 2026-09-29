/*===========================================================================
  RegTrack Insights - v2 (2026-09-29): Licence status = RegTrack's own label
  DICTIONARY - Semantic 'LicenceReportStatus'

  Product decision (2026-09-29, raised by the head of testing): the Licence
  dimension must count Active / Expired / ... exactly as the Status column of
  RegTrack's own licence report (SP_LicenseMyReport_V2) shows them. Before
  this, Insights decided Active/Lapsed from EndDate, so tenant 1285's
  Transport showed 9-10 Active where RegTrack shows 5.

  RegTrack builds that column from the latest status NAME with a CASE
  (SP_LicenseMyReport_V2, the "AS Status" column). Names are duplicated with
  whitespace variants on Lic_tbl_StatusMaster (13/14, 15/16), so Insights
  never matches names: each ID is mapped here ONCE to the label RegTrack's
  CASE produces for that ID's name. Verified 2026-09-29 against RegTrack's
  own output for user 11416 / tenant 1285 (StatusID x Status crosstab,
  180 licences: 2->Active, 3->Expired, 5->Applied, 6/7/13/15/17->
  PendingForReview, 8->Rejected, 12->Terminated, 14->Applied,
  16->Not Applicable, 18->Application Rejected).
  IDs with no CASE branch show their own name (ELSE RLTV.Status):
  1 Draft, 9 Registered, 10 Registered & Renewal Filed, 11 Validity Expired.

  Seeded into BOTH dictionary versions 1 and 2, so re-running
  01_classification_dictionary_v2.sql (which recopies v1 into v2) keeps it,
  and its v1 = v2 row-count guard still holds. The v1 licence proc never
  reads this semantic.

  ASCII only. Atomic. Install after 01, before 23.
===========================================================================*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @map TABLE (StatusId INT PRIMARY KEY, Label NVARCHAR(60) NOT NULL, Note NVARCHAR(200) NOT NULL);
    INSERT @map VALUES
        ( 1, N'Draft',                      N'Draft - no CASE branch, RegTrack shows the name'),
        ( 2, N'Active',                     N'Active'),
        ( 3, N'Expired',                    N'Expired'),
        ( 4, N'Expiring',                   N'Expiring'),
        ( 5, N'Applied',                    N'Applied'),
        ( 6, N'PendingForReview',           N'Applied but Pending For Renewal'),
        ( 7, N'PendingForReview',           N'Renewed'),
        ( 8, N'Rejected',                   N'Rejected'),
        ( 9, N'Registered',                 N'Registered - no branch in the Status column CASE'),
        (10, N'Registered & Renewal Filed', N'Registered & Renewal Filed - no CASE branch'),
        (11, N'Validity Expired',           N'Validity Expired - no CASE branch, NOT the Expired label'),
        (12, N'Terminated',                 N'Terminated'),
        (13, N'PendingForReview',           N'Applied for Assessment (single space)'),
        (14, N'Applied',                    N'Applied for  Assessment (double space)'),
        (15, N'PendingForReview',           N'Not Applicable (single space)'),
        (16, N'Not Applicable',             N'Not  Applicable (double space)'),
        (17, N'PendingForReview',           N'Terminated_P'),
        (18, N'Application Rejected',       N'Application Rejected - no CASE branch');

    DELETE FROM dbo.InsightsEnumPolarity
     WHERE Semantic = 'LicenceReportStatus' AND VersionId IN (1, 2);

    INSERT dbo.InsightsEnumPolarity (VersionId, Semantic, RawValue, Meaning, AppliesTo, Notes)
    SELECT v.VersionId, 'LicenceReportStatus', CAST(m.StatusId AS VARCHAR(10)), m.Label,
           N'Lic_tbl_StatusMaster.ID',
           N'RegTrack licence report Status label. ' + m.Note
    FROM @map m
    CROSS JOIN (VALUES (1), (2)) v(VersionId);

    /*  Every status id RegTrack has - deleted ones too (13/14 are deleted
        but still the latest status of live licences) - must have a label. */
    IF EXISTS (SELECT 1 FROM Lic_tbl_StatusMaster sm
               WHERE NOT EXISTS (SELECT 1 FROM @map m WHERE m.StatusId = sm.ID))
        THROW 51006, N'LICENCE STATUS LABELS - Lic_tbl_StatusMaster has an id with no RegTrack label in this seed. Rolled back.', 1;

    COMMIT TRANSACTION;
    PRINT 'LicenceReportStatus labels seeded (dictionary v1 and v2).';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
