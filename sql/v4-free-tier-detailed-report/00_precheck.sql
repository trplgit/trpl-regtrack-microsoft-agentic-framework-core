/*===========================================================================
  RegTrack Insights - v4 free tier: follow RegTrack's Detailed Report
  00_precheck.sql - READ-ONLY. Run FIRST, on the target database.

  Part 1 tells you which version each of the 8 free-tier procedures is on, and
  refuses if any is something this pack was not built from:
    OLD        - before v4 (dashboard rules). Demo is expected here.
    V4 EARLY   - v4 as deployed on prod/UAT 2026-10-06, without the 2026-10-07
                 licence end-date fix. Prod and UAT are expected here.
    V4         - already this pack (re-running the install is harmless).
    CHANGED    - none of the above: STOP, send the message to the author.
  Part 2 checks the reference data the new loaders depend on (G1-G4).

  Success = 'PRECHECK OK' printed last. Failure = a THROW. Nothing is changed.
  Error codes (free range per CLAUDE.md Sec.5b, not read by the C# refusal window):
    51181 procedure drift     51182 G1 licence report labels
    51183 G2 timed_by_close   51184 G3 overdue-eligible set   51185 G4 status names
===========================================================================*/
SET NOCOUNT ON;

DECLARE @known TABLE (Name SYSNAME PRIMARY KEY, OldSha VARCHAR(66), EarlySha VARCHAR(66), V4Sha VARCHAR(66));
INSERT @known (Name, OldSha, EarlySha, V4Sha) VALUES
 ('usp_Insights_FreeMonthly_Act', '0x719815AD557406A2F70F25AEE71563402D778B59318EF0FE72F947277CC1AE02', '0xA0E21AC4B18958A9E1330B83C29DA93D3089DE32D4D0CF3BA9E777894153F848', '0xA0E21AC4B18958A9E1330B83C29DA93D3089DE32D4D0CF3BA9E777894153F848'),
 ('usp_Insights_FreeMonthly_Licence', '0x1E29CDC67C98FBD9C70D938FA20CE893173A2F58406F125532275BD4F97737A1', '0x86C3F16DD6E1712D8601E4D7B803A6BFE636BB381A25D6122973772771E48926', '0xFDD637F717DD1FED2FB9055F996ED97B35D02A26F9516165DB7B0FEECF96F7E4'),
 ('usp_Insights_FreeMonthly_LoadFacts', '0xA19C2CE92FAD444A1794D146FB213E34F34A3547355074763223BD09BA62EFEE', '0x522AAD9D8DED71AB28C0661F18380288106F52E511D894A9A3A00B950F9D1BDF', '0x522AAD9D8DED71AB28C0661F18380288106F52E511D894A9A3A00B950F9D1BDF'),
 ('usp_Insights_FreeMonthly_LoadLicences', '0x48050163481848CCE6F2E8B0D60B628A397C18DD4C0A7ED578B93854F84BB363', '0x14889F7381E21D843F91150F2D814E8214EBB097FA8C8628F8BE14C5871AB6EC', '0x6AAE3575DEBCDA7BE8480DBDA4223FB6EA1BF03CCE1D3611F7B65C1A81F6CED1'),
 ('usp_Insights_FreeMonthly_Location', '0x42CFD4F46836C36E69F81E2981FBB49F75D2FE886220C3C36D180125E893AF43', '0xB36F636506263C7C541BB45B2CE120286A3905073709C96656FDBBDCAC8BA44A', '0xB36F636506263C7C541BB45B2CE120286A3905073709C96656FDBBDCAC8BA44A'),
 ('usp_Insights_FreeMonthly_MemberDetectors', '0xEC9CEEFDEAA5E74295F881C0C08E4FF279411B925A655506CE7729A2FD0234C9', '0x29AA41BF3FAE986969357F6D15E5A3AFD6D9437F588878BABC9EDAD41890EE37', '0x29AA41BF3FAE986969357F6D15E5A3AFD6D9437F588878BABC9EDAD41890EE37'),
 ('usp_Insights_FreeMonthly_Overview', '0x5B9B777CDE6FAA1175DCD293BEFABB81169F4DABD898368E760BCB6DFA864AB2', '0x8A96985EBD9BDAE460E2247D32FEB5C3947C433AA4CC415CED06B939045FCDF5', '0xAD71D7B78AE57157DBE640C648AE7A7AC0D9D3C26410AE824AA3578758B08727'),
 ('usp_Insights_FreeMonthly_Users', '0x5B703FA59A8F90D3330489DF2DDBF9A00121F8D1810DE9A3D289EBE7F626A830', '0x5CE4C1CFAA0E2EE5ED00AF34DC5BF8FC1FD666FECDB1F4AF2941874E4F6FA929', '0x5CE4C1CFAA0E2EE5ED00AF34DC5BF8FC1FD666FECDB1F4AF2941874E4F6FA929');

DECLARE @check TABLE (Name SYSNAME, Verdict VARCHAR(10));
INSERT @check (Name, Verdict)
SELECT k.Name,
       CASE WHEN a.Sha IS NULL       THEN 'MISSING'
            WHEN a.Sha = k.V4Sha     THEN 'V4'
            WHEN a.Sha = k.EarlySha  THEN 'V4 EARLY'
            WHEN a.Sha = k.OldSha    THEN 'OLD'
            ELSE 'CHANGED' END
FROM @known k
OUTER APPLY (SELECT CONVERT(VARCHAR(66), HASHBYTES('SHA2_256', CAST(m.definition AS VARBINARY(MAX))), 1) AS Sha
             FROM sys.sql_modules m WHERE m.object_id = OBJECT_ID('dbo.' + k.Name)) a;

SELECT Name, Verdict FROM @check ORDER BY Name;

IF EXISTS (SELECT 1 FROM @check WHERE Verdict IN ('CHANGED', 'MISSING'))
BEGIN
    DECLARE @msg NVARCHAR(2048) = N'PRECHECK FAILED - these procedures are not a version this pack was built from: '
        + (SELECT STRING_AGG(Name + ' (' + Verdict + ')', ', ') FROM @check WHERE Verdict IN ('CHANGED', 'MISSING'))
        + N'. Do NOT run the install. Send this message to the author.';
    THROW 51181, @msg, 1;
END

PRINT 'PRECHECK 1/5 OK - all 8 free-tier procedures are a known version (OLD / V4 EARLY / V4).';
/*---------------------------------------------------------------------------
  Reference-data gates. The new loaders read these rows; if any is missing or
  different the emails would refuse (or silently mean something else).
---------------------------------------------------------------------------*/

/* G1 - licence report labels (current dictionary version): ids 2/3/4 must be
   Active/Expired/Expiring, and every Lic_tbl_StatusMaster id must be labelled. */
DECLARE @lic TABLE (RawValue VARCHAR(10) PRIMARY KEY, Meaning NVARCHAR(200));
INSERT @lic (RawValue, Meaning)
SELECT p.RawValue, p.Meaning
FROM dbo.InsightsEnumPolarity p
JOIN dbo.InsightsDictionaryVersion v ON v.VersionId = p.VersionId AND v.IsCurrent = 1
WHERE p.Semantic = 'LicenceReportStatus';

IF NOT EXISTS (SELECT 1 FROM @lic WHERE RawValue = '2' AND Meaning = N'Active')
   OR NOT EXISTS (SELECT 1 FROM @lic WHERE RawValue = '3' AND Meaning = N'Expired')
   OR NOT EXISTS (SELECT 1 FROM @lic WHERE RawValue = '4' AND Meaning = N'Expiring')
   OR EXISTS (SELECT 1 FROM Lic_tbl_StatusMaster sm
              LEFT JOIN @lic l ON l.RawValue = CAST(sm.ID AS VARCHAR(10))
              WHERE l.RawValue IS NULL)
    THROW 51182, N'PRECHECK FAILED (G1) - LicenceReportStatus labels are missing or wrong in the current dictionary version. Deploy sql/v2/22_licence_report_status_dictionary_v2.sql first. Do NOT run the install.', 1;
PRINT 'PRECHECK 2/5 OK - licence report labels present.';

/* G2 - Approved ids timed by close date must be exactly {7, 9}. */
IF (SELECT COUNT(*) FROM dbo.InsightsFreeDashboardStatusRule WHERE RuleName = 'timed_by_close_date') <> 2
   OR EXISTS (SELECT 1 FROM dbo.InsightsFreeDashboardStatusRule
              WHERE RuleName = 'timed_by_close_date' AND StatusId NOT IN (7, 9))
    THROW 51183, N'PRECHECK FAILED (G2) - InsightsFreeDashboardStatusRule timed_by_close_date is not exactly {7, 9}. Do NOT run the install; send this to the author.', 1;
PRINT 'PRECHECK 3/5 OK - timed_by_close_date = {7, 9}.';

/* G3 - the current dictionary must treat ONLY status 1 (Open) as overdue-eligible,
   which is what the Detailed Report calls Overdue. */
IF (SELECT COUNT(*) FROM dbo.vInsightsStatusCurrent WHERE OverdueEligible = 1) <> 1
   OR NOT EXISTS (SELECT 1 FROM dbo.vInsightsStatusCurrent WHERE OverdueEligible = 1 AND StatusId = 1)
    THROW 51184, N'PRECHECK FAILED (G3) - the current status dictionary does not mark exactly status 1 as overdue-eligible. Deploy sql/v2/01_classification_dictionary_v2.sql first. Do NOT run the install.', 1;
PRINT 'PRECHECK 4/5 OK - overdue-eligible set = {1}.';

/* G4 - ComplianceStatus names, which the Detailed Report buckets by. A MISSING id
   is allowed (UAT has no id 20); a DIFFERENT name is not. Exact, binary compare. */
DECLARE @names TABLE (ID INT PRIMARY KEY, Name NVARCHAR(100) NOT NULL);
INSERT @names (ID, Name) VALUES
 (1, N'Open'), (2, N'Complied but pending review'), (3, N'Complied Delayed but pending review'),
 (4, N'Closed-Timely'), (5, N'Closed-Delayed'), (6, N'Rejected'), (7, N'Approved'), (8, N'Rejected'),
 (9, N'Approved'), (10, N'In Progress'), (11, N'Revise Compliance'), (12, N'Submitted For Interim Review'),
 (13, N'Interim Review Approved'), (14, N'Interim Rejected'), (15, N'Not Applicable'),
 (16, N'Not  Complied'), (17, N'Not Complied'), (18, N'Not  Applicable'),
 (19, N'Complied But Document Pending'), (20, N'Pending for Performer Action'),
 (21, N'Deviation Applied'), (22, N'Deviation Rejected'), (23, N'Deviation Approved');

DECLARE @badNames NVARCHAR(1000) =
    (SELECT STRING_AGG(CAST(cs.ID AS VARCHAR(10)), ', ')
     FROM ComplianceStatus cs
     LEFT JOIN @names n ON n.ID = cs.ID
     WHERE n.ID IS NULL
        OR CAST(cs.Name AS NVARCHAR(100)) COLLATE Latin1_General_BIN2 <> n.Name COLLATE Latin1_General_BIN2
        OR DATALENGTH(CAST(cs.Name AS NVARCHAR(100))) <> DATALENGTH(n.Name));
IF @badNames IS NOT NULL
BEGIN
    DECLARE @g4 NVARCHAR(2048) = N'PRECHECK FAILED (G4) - ComplianceStatus ids with an unexpected or changed name: ' + @badNames
        + N'. The Detailed Report buckets statuses by name, so the mapping would be wrong. Do NOT run the install; send this to the author.';
    THROW 51185, @g4, 1;
END
PRINT 'PRECHECK 5/5 OK - ComplianceStatus names as expected.';

PRINT 'PRECHECK OK - safe to run the install (01 -> 02 -> 03 -> 04).';
