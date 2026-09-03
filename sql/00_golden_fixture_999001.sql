/*===========================================================================
  RegTrack Insights - Golden Fixture (docs/GOLDEN_FIXTURES.md)

  Builds tenant 999001 (F-1..F-13) and tenant 999002 (F-14, single-branch, kept
  separate - see docs/GOLDEN_FIXTURES.md's note on why) against a BLANK SQL Server
  container. Never run against a shared/production database - see the F-7 warning
  below for why.

  Generated from real UAT metadata (sys.columns/sys.types/sys.default_constraints)
  and real donor rows (Compliance 3/5, Act 5/32) - not hand-typed DDL. Validated
  live: usp_Insights_GoldenInvariants passes all checks for both 999001 and 999002,
  aggregate expectations match exactly (past-due=80, overdue=40, completed=20,
  resolved_terminal=20, on-time=50.0%, F-6 rollup=25), and F-12/F-13/F-14's flags
  (no_obligations_configured, degraded_peer_sample, comparatives suppressed) all
  fired correctly via usp_Insights_Dimension_Location.

  NOT validated end to end in a real container in this session - Docker was
  installed but its daemon was not running here. Everything above was proven
  against real UAT instead (same schema, same procs); the risk this leaves open is
  narrow: whether this SCRIPT reproduces that schema byte-for-byte in a blank
  container. One gap was already found and fixed this way (a missing DEFAULT
  constraint on ComplianceInstance.DirectorId) - treat this file as reviewed but
  UNPROVEN until a real container run confirms it.

  -- [FIX] CustomerBranch.Status is nullable with no DEFAULT (confirmed live -
  -- see sys.columns above). Every fixture branch INSERT below now sets it to 1
  -- explicitly; without that they would default to NULL and every one would be
  -- excluded from every dimension the moment sql/01-17 started filtering
  -- Status=1, silently zeroing out this entire fixture. No fixture branch tests
  -- Status=0 (deactivated) specifically yet - that is real gap, not covered by
  -- this fix, worth its own F-series case (mirroring how F-7's soft-deleted
  -- branch is tested) before shipping the estate-definition change.
===========================================================================*/

-- Generated from real UAT schema (sys.columns/sys.types) for the golden-fixture CI container.
-- These are the base RegTrack platform tables sql/01..sql/14 assume already exist.
-- No FK constraints - nothing in the Insights SQL depends on FK enforcement.
SET NOCOUNT ON;

IF OBJECT_ID('dbo.Customer', 'U') IS NULL
BEGIN
CREATE TABLE [dbo].[Customer] (
    [ID] int IDENTITY(1,1) NOT NULL,
    [Name] varchar(MAX) NOT NULL,
    [Address] varchar(MAX) NOT NULL,
    [Industry] int NULL,
    [BuyerName] varchar(MAX) NOT NULL,
    [BuyerContactNumber] varchar(32) NOT NULL,
    [BuyerEmail] varchar(MAX) NULL,
    [CreatedOn] datetime NOT NULL CONSTRAINT [DF_Customer_CreatedOn] DEFAULT (getdate()),
    [IsDeleted] bit NOT NULL CONSTRAINT [DF_Customer_IsDeleted] DEFAULT ((0)),
    [StartDate] datetime NULL,
    [EndDate] datetime NULL,
    [DiskSpace] varchar(MAX) NULL,
    [Status] int NULL,
    [IComplianceApplicable] int NULL,
    [LocationType] int NULL,
    [VerticalApplicable] int NULL,
    [TaskApplicable] int NULL,
    [IsServiceProvider] bit NULL,
    [ServiceProviderID] int NULL CONSTRAINT [DF_Customer_ServiceProviderID] DEFAULT ((0)),
    [GSTNum] varchar(MAX) NULL,
    [CRegNum] varchar(MAX) NULL,
    [ComplianceProductType] int NULL CONSTRAINT [DF_Customer_ComplianceProductType] DEFAULT ((0)),
    [LogoPath] varchar(MAX) NULL,
    [IsPayment] bit NULL CONSTRAINT [DF_Customer_IsPayment] DEFAULT ((0)),
    [IsDistributor] bit NULL CONSTRAINT [DF_Customer_IsDistributor] DEFAULT ((0)),
    [ParentID] int NULL CONSTRAINT [DF_Customer_ParentID] DEFAULT (NULL),
    [CanCreateSubDist] bit NULL CONSTRAINT [DF_Customer_CanCreateSubDist] DEFAULT ((1)),
    [IsLabelApplicable] int NULL,
    [IsLock] bit NOT NULL CONSTRAINT [DF_Customer_IsLock] DEFAULT ((0)),
    [LockingDays] int NOT NULL CONSTRAINT [DF_Customer_LockingDays] DEFAULT ((0)),
    [CreatedBy] bigint NULL,
    [CreatedFrom] int NULL,
    [DocManNonMan] int NULL,
    [IsEventConfigurable] bit NULL,
    [EscalationDays] int NULL,
    [IsChecklistmain] int NULL,
    [IsAssessment] bit NULL,
    [IsComplainceCertificate] int NULL,
    [IsCertificateApplicable] bit NULL,
    [IsCertificateEntity] bit NULL CONSTRAINT [DF_Customer_IsCertificateEntity] DEFAULT ((0)),
    [UpdatedOn] datetime NULL,
    [IsDOCAIPartialDocUploadAllowed] bit NULL,
    CONSTRAINT [PK_Customer] PRIMARY KEY ([ID])
);
END
GO

IF OBJECT_ID('dbo.CustomerBranch', 'U') IS NULL
BEGIN
CREATE TABLE [dbo].[CustomerBranch] (
    [Name] varchar(500) NULL,
    [CustomerID] int NOT NULL,
    [Type] tinyint NOT NULL,
    [LegalRelationShipOrStatus] tinyint NULL,
    [AddressLine1] varchar(MAX) NULL,
    [AddressLine2] varchar(300) NULL,
    [StateID] int NOT NULL,
    [CityID] int NOT NULL,
    [Others] varchar(100) NULL,
    [Industry] int NULL,
    [ContactPerson] varchar(MAX) NULL,
    [Landline] varchar(MAX) NULL,
    [Mobile] varchar(MAX) NULL,
    [EmailID] varchar(MAX) NULL,
    [IsDeleted] bit NOT NULL,
    [CreatedOn] datetime NOT NULL,
    [ID] int IDENTITY(1,1) NOT NULL,
    [ParentID] int NULL,
    [PinCode] varchar(10) NULL,
    [LegalRelationShipID] int NULL,
    [Status] int NULL,
    [LegalEntityTypeID] int NULL,
    [ComType] int NULL,
    [AuditPR] bit NOT NULL CONSTRAINT [DF_CustomerBranch_AuditPR] DEFAULT ((0)),
    [GSTNumber] varchar(MAX) NULL,
    [CreatedBy] bigint NULL,
    [Zone] varchar(MAX) NULL,
    [Region] varchar(MAX) NULL,
    [Territory] varchar(MAX) NULL,
    [CreatedFrom] int NULL,
    [IsForeignAddress] bit NULL,
    [UpdatedOn] datetime NULL,
    [BrandID] int NULL,
    [StoreCode] nvarchar(50) NULL,
    [OpenStoreDate] datetime NULL,
    [LC_UniqueID] varchar(MAX) NULL,
    CONSTRAINT [PK_CustomerBranch] PRIMARY KEY ([ID])
);
END
GO

IF OBJECT_ID('dbo.User', 'U') IS NULL
BEGIN
CREATE TABLE [dbo].[User] (
    [ID] bigint IDENTITY(1,1) NOT NULL,
    [FirstName] varchar(100) NOT NULL,
    [LastName] varchar(100) NOT NULL,
    [Email] varchar(200) NULL,
    [Password] nvarchar(512) NULL,
    [ContactNumber] varchar(32) NULL,
    [Address] varchar(MAX) NULL,
    [CreatedOn] datetime NOT NULL CONSTRAINT [DF_User_CreatedOn] DEFAULT (getdate()),
    [IsDeleted] bit NOT NULL CONSTRAINT [DF_User_IsDeleted] DEFAULT ((0)),
    [Designation] varchar(100) NULL,
    [DepartmentID] int NULL,
    [CreatedBy] bigint NULL,
    [CreatedByText] varchar(250) NOT NULL,
    [IsActive] bit NOT NULL CONSTRAINT [DF_User_IsActive] DEFAULT ((1)),
    [DeactivatedOn] datetime NULL,
    [CustomerID] int NULL,
    [CustomerBranchID] int NULL,
    [ReportingToID] bigint NULL,
    [RoleID] int NOT NULL CONSTRAINT [DF_User_RoleID] DEFAULT ((1)),
    [LastLoginTime] datetime NULL,
    [ChangPasswordDate] datetime NULL,
    [ChangePasswordFlag] bit NULL,
    [NotLongerLoginDate] datetime NULL,
    [WrongAttempt] int NOT NULL CONSTRAINT [DF_User_WrongAttempt] DEFAULT ((0)),
    [IsHead] bit NULL CONSTRAINT [DF_User_IsHead] DEFAULT ((0)),
    [AuditorID] bigint NULL,
    [IsAuditHeadOrMgr] char(2) NULL,
    [SendEmail] bit NULL,
    [ImagePath] varchar(MAX) NULL,
    [ImageName] varchar(MAX) NULL,
    [LawyerFirmID] int NULL,
    [IsExternal] bit NULL,
    [LitigationRoleID] int NULL,
    [Startdate] datetime NULL,
    [Enddate] datetime NULL,
    [AuditStartPeriod] datetime NULL,
    [AuditEndPeriod] datetime NULL,
    [ContractRoleID] int NULL,
    [UserName] varchar(MAX) NULL,
    [LicenseRoleID] int NULL,
    [PAN] varchar(MAX) NULL,
    [VendorRoleID] int NULL,
    [DesktopRestrict] bit NULL,
    [IMEINumber] varchar(50) NULL,
    [IMEIChecked] bit NULL,
    [EnType] varchar(10) NULL,
    [MobileAccess] bit NULL,
    [SecretarialRoleID] int NULL,
    [HRRoleID] int NULL,
    [InsiderRoleID] int NULL,
    [SSOAccess] bit NULL CONSTRAINT [DF_User_SSOAccess] DEFAULT ((0)),
    [Cer_OwnerRoleID] int NULL,
    [Cer_OfficerRoleID] int NULL,
    [LogOutURL] varchar(MAX) NULL,
    [CreatedFrom] int NULL,
    [IsCertificateVisible] int NULL,
    [VendorAuditRoleID] int NULL,
    [UserType] int NULL,
    [LitigationType] int NULL,
    [TaxType] varchar(50) NULL,
    [LocationOrCentralUser] int NULL,
    [LitigationCustomerBranchID] varchar(MAX) NULL,
    [isAssignerRoleID] int NULL CONSTRAINT [DF_User_isAssignerRoleID] DEFAULT ((0)),
    [CustomerMangement_MasterAsscess] int NULL,
    [CustomerMangement_CalenderAccess] int NULL,
    [IsRelatedParty] bit NULL CONSTRAINT [DF_User_IsRelatedParty] DEFAULT ((0)),
    [RelatedPartyRoleID] int NULL,
    [RelatedPartySubRoleID] int NULL,
    [DIN] varchar(100) NULL,
    [UpdatedOn] datetime NULL,
    [IsAllowConcurrentLogin] bit NULL,
    [MiddleName] varchar(100) NULL,
    [PasswordPolicyType] varchar(10) NULL,
    [PasswordPolicyExpiryDay] int NULL,
    [PasswordPolicyUpdatedOn] datetime NULL,
    [Tokenquota] bigint NULL,
    [Usedquota] bigint NULL,
    [ManagerID] varchar(100) NULL,
    [ObjectID] varchar(100) NULL,
    [MobilePlatformForceLogout] bit NOT NULL CONSTRAINT [DF_User_MobilePlatformForceLogout] DEFAULT ((0)),
    [AppVersion_DeviceName] nvarchar(100) NULL,
    [CurrentVersion] nvarchar(250) NULL,
    [OlderVersion] nvarchar(250) NULL,
    [Cer_CoordinatorRoleID] int NULL,
    CONSTRAINT [PK_User] PRIMARY KEY ([ID])
);
END
GO

IF OBJECT_ID('dbo.ComplianceInstance', 'U') IS NULL
BEGIN
CREATE TABLE [dbo].[ComplianceInstance] (
    [ID] bigint IDENTITY(1,1) NOT NULL,
    [ComplianceId] bigint NOT NULL,
    [CreatedOn] datetime NOT NULL CONSTRAINT [DF_ComplianceInstance_CreatedOn] DEFAULT (getdate()),
    [IsDeleted] bit NOT NULL CONSTRAINT [DF_ComplianceInstance_IsDeleted] DEFAULT ((0)),
    [ScheduledOn] datetime NOT NULL,
    [CustomerBranchID] int NOT NULL,
    [GenerateSchedule] bit NOT NULL CONSTRAINT [DF_ComplianceInstance_GenerateSchedule] DEFAULT ((1)),
    [IsAvantis] bit NULL,
    [DepartmentID] bigint NULL,
    [Risk] tinyint NULL,
    [SequenceID] varchar(25) NULL,
    [DirectorId] bigint NOT NULL CONSTRAINT [DF_ComplianceInstance_DirectorId] DEFAULT ((0)),
    [IsSecretarial] bit NULL CONSTRAINT [DF_ComplianceInstance_IsSecretarial] DEFAULT ((0)),
    [Cer_OwnerUserID] int NULL,
    [Cer_OfficerUserID] int NULL,
    [IsDocMan_NonMan] bit NULL,
    [ChecklistFlowFrom] datetime NULL,
    [IsChecklistWorkFlow] bit NULL,
    [UpdatedOn] datetime NULL,
    [DeviationReviewerID] bigint NULL,
    [IsSmetaCompliance] bit NULL,
    [ProofofCompliance] varchar(MAX) NULL,
    CONSTRAINT [PK_ComplianceInstance] PRIMARY KEY ([ID])
);
END
GO

IF OBJECT_ID('dbo.ComplianceScheduleOn', 'U') IS NULL
BEGIN
CREATE TABLE [dbo].[ComplianceScheduleOn] (
    [ID] bigint IDENTITY(1,1) NOT NULL,
    [ScheduleOn] datetime NOT NULL,
    [ComplianceInstanceID] bigint NULL,
    [ForMonth] varchar(200) NULL,
    [ForPeriod] bigint NULL,
    [IsActive] bit NULL,
    [IsUpcomingNotDeleted] bit NULL,
    [EventScheduledOnID] bigint NULL,
    [ParentEventD] bigint NULL,
    [IntermediateEventID] bigint NULL,
    [SubEventID] bigint NULL,
    [RLCS_PayrollMonth] varchar(2) NULL,
    [RLCS_PayrollYear] varchar(4) NULL,
    [RLCS_ActivityEndDate] datetime NULL,
    [ActualScheduleon] datetime NULL,
    [IsDocMan_NonMan] bit NULL,
    [Performerid] bigint NULL,
    [Reviewerid] bigint NULL,
    [Approverid] bigint NULL,
    [ExpectedDate] datetime NULL,
    [Remark] varchar(1000) NULL,
    [OriginalDueDate] datetime NULL,
    [TargetDate] datetime NULL,
    [StartDate] datetime NULL,
    [EndDate] datetime NULL,
    [IsChecklistWorkFlow] bit NULL,
    [UpdatedOn] datetime NULL,
    [MeetingId] bigint NULL,
    [EventNature] nvarchar(MAX) NULL,
    [ChatCount] bigint NULL,
    CONSTRAINT [PK_ComplianceScheduleOn] PRIMARY KEY ([ID])
);
END
GO

IF OBJECT_ID('dbo.ComplianceTransaction', 'U') IS NULL
BEGIN
CREATE TABLE [dbo].[ComplianceTransaction] (
    [ID] bigint IDENTITY(1,1) NOT NULL,
    [ComplianceInstanceId] bigint NOT NULL,
    [StatusId] int NOT NULL,
    [Remarks] varchar(MAX) NOT NULL,
    [Dated] datetime NOT NULL CONSTRAINT [DF_ComplianceTransaction_Dated] DEFAULT (getdate()),
    [CreatedBy] bigint NOT NULL,
    [CreatedByText] varchar(250) NULL,
    [FileID] int NULL,
    [StatusChangedOn] datetime NULL,
    [ComplianceScheduleOnID] bigint NULL,
    [Interest] decimal(18,2) NULL,
    [Penalty] decimal(18,2) NULL,
    [ComplianceSubTypeID] int NULL,
    [IsPenaltySave] bit NULL,
    [ValuesAsPerSystem] decimal(18,2) NULL,
    [ValuesAsPerReturn] decimal(18,2) NULL,
    [LiabilityPaid] decimal(18,2) NULL,
    [PenaltySubmit] varchar(2) NULL,
    [OUserID] bigint NULL,
    [PhysicalLocation] varchar(MAX) NULL,
    [FileNO] varchar(50) NULL,
    [ChallanNo] varchar(MAX) NULL,
    [Challanpaiddate] datetime NULL,
    [ChallanAmount] varchar(MAX) NULL,
    [BankName] varchar(MAX) NULL,
    [RemarkTypeID] int NULL,
    [PerformBy] varchar(50) NULL,
    [UpdatedOn] datetime NULL,
    [DeviationReason] varchar(MAX) NULL,
    [DeviationClosureDate] datetime NULL,
    [CommonUserID] bigint NULL,
    [AccountDetail] bigint NULL,
    [DocumentNo] varchar(100) NULL,
    [Payment] decimal(18,2) NULL,
    CONSTRAINT [PK_ComplianceTransaction] PRIMARY KEY ([ID])
);
END
GO

IF OBJECT_ID('dbo.EntitiesAssignment', 'U') IS NULL
BEGIN
CREATE TABLE [dbo].[EntitiesAssignment] (
    [ID] int IDENTITY(1,1) NOT NULL,
    [UserID] bigint NOT NULL,
    [BranchID] bigint NOT NULL,
    [ComplianceCatagoryID] bigint NOT NULL,
    [CreatedOn] datetime NULL,
    [UpdatedOn] datetime NULL,
    CONSTRAINT [PK_EntitiesAssignment] PRIMARY KEY ([ID])
);
END
GO

IF OBJECT_ID('dbo.ComplianceStatus', 'U') IS NULL
BEGIN
CREATE TABLE [dbo].[ComplianceStatus] (
    [ID] int IDENTITY(1,1) NOT NULL,
    [Name] varchar(500) NOT NULL,
    [NewStatus] varchar(300) NULL,
    CONSTRAINT [PK_ComplianceStatus] PRIMARY KEY ([ID])
);
END
GO

IF OBJECT_ID('dbo.Compliance', 'U') IS NULL
BEGIN
CREATE TABLE [dbo].[Compliance] (
    [ID] bigint IDENTITY(1,1) NOT NULL,
    [Description] varchar(MAX) NULL,
    [UploadDocument] bit NULL,
    [Frequency] tinyint NULL,
    [DueDate] int NULL,
    [RiskType] tinyint NULL,
    [NonComplianceType] tinyint NULL,
    [NonComplianceEffects] varchar(MAX) NULL,
    [CreatedOn] datetime NOT NULL CONSTRAINT [DF_Compliance_CreatedOn] DEFAULT (getdate()),
    [IsDeleted] bit NOT NULL CONSTRAINT [DF_Compliance_IsDeleted] DEFAULT ((0)),
    [ActID] int NOT NULL,
    [Sections] varchar(MAX) NULL,
    [ComplianceType] tinyint NOT NULL,
    [FixedMinimum] float NULL,
    [FixedMaximum] float NULL,
    [VariableAmountPerDay] float NULL,
    [VariableAmountPerDayMax] float NULL,
    [Imprisonment] bit NULL,
    [Designation] varchar(MAX) NULL,
    [MinimumYears] int NULL,
    [MaximumYears] int NULL,
    [Others] varchar(500) NULL,
    [RequiredForms] varchar(MAX) NULL,
    [NatureOfCompliance] tinyint NULL,
    [VariableAmountPerMonth] float NULL,
    [ShortDescription] varchar(MAX) NULL,
    [EventID] bigint NULL,
    [EventComplianceType] tinyint NULL,
    [SubComplianceType] tinyint NULL,
    [FixedGap] int NULL,
    [ReminderType] tinyint NULL CONSTRAINT [DF_Compliance_ReminderType] DEFAULT ((0)),
    [ReminderBefore] int NULL,
    [ReminderGap] int NULL,
    [SubEventID] bigint NULL,
    [CheckListTypeID] int NULL,
    [OneTimeDate] datetime NULL,
    [PenaltyDescription] varchar(MAX) NULL,
    [ReferenceMaterialText] varchar(MAX) NULL,
    [EffectiveDate] datetime NULL,
    [EventFlag] bit NULL,
    [CreatedBy] bigint NULL,
    [UpDocs] bit NULL,
    [ComplinceVisible] bit NULL,
    [Status] char(1) NULL,
    [DeactivateOn] datetime NULL,
    [DeactivateDesc] varchar(MAX) NULL,
    [SampleFormLink] varchar(MAX) NULL,
    [ComplianceSubTypeID] int NULL,
    [UpdatedOn] datetime NULL,
    [DueWeekDay] tinyint NULL,
    [StartDate] datetime NULL,
    [ShortForm] varchar(MAX) NULL,
    [duedatetype] int NULL,
    [onlineoffline] bit NULL,
    [IsFrequencyBased] bit NULL,
    [TriggerNo] int NULL,
    [triggerNoOfDays] int NULL,
    [IntervalDays] int NULL,
    [IsForcefulClosure] bit NULL,
    [MinimumPeriodType] int NULL,
    [MaximumPeriodType] int NULL,
    [VariableAmountPerInstance] float NULL,
    [LocationTypeID] bigint NULL,
    [IsForSecretarial] bit NULL CONSTRAINT [DF_Compliance_IsForSecretarial] DEFAULT ((0)),
    [VariableAmountPercent] nvarchar(500) NULL,
    [VariableAmountPercentMax] nvarchar(500) NULL,
    [ScheduleType] int NULL,
    [IsMapped] bit NULL,
    [WorkingDays] int NULL,
    [IsSatuarday] bit NULL,
    [ComplianceActionableProcedure] varchar(MAX) NULL,
    [WeekDay] int NULL,
    [WeekNo] int NULL,
    [ComplianceFormId] int NULL,
    [IsSecretarialIntermediateCompliance] bit NULL,
    [DueTypeMode] tinyint NULL,
    [WeeklyDueDays] varchar(20) NULL,
    CONSTRAINT [PK_Compliance] PRIMARY KEY ([ID])
);
END
GO

IF OBJECT_ID('dbo.Act', 'U') IS NULL
BEGIN
CREATE TABLE [dbo].[Act] (
    [ID] int IDENTITY(1,1) NOT NULL,
    [Name] varchar(MAX) NOT NULL,
    [Description] varchar(MAX) NULL,
    [State] varchar(100) NULL,
    [City] varchar(100) NULL,
    [ComplianceTypeId] int NULL,
    [ComplianceCategoryId] int NULL,
    [CreatedOn] datetime NOT NULL CONSTRAINT [DF_Act_CreatedOn] DEFAULT (getdate()),
    [IsDeleted] bit NOT NULL CONSTRAINT [DF_Act_IsDeleted] DEFAULT ((0)),
    [StateID] int NULL,
    [CityID] int NULL,
    [CreatedBy] bigint NULL,
    [UpdatedOn] datetime NULL,
    [ProductCode] char(1) NULL,
    [StartDate] datetime NULL,
    [Act_DeptID] int NULL,
    [MinistryID] int NULL,
    [RegulatorID] int NULL,
    [duedatetype] int NULL,
    [ActGroup] varchar(10) NULL,
    [ShortForm] varchar(MAX) NULL,
    [CountryID] int NULL,
    [Actapplicabiltyrules] varchar(MAX) NULL,
    [LocationTypeID] bigint NULL,
    [ActGroupId] bigint NULL,
    [IsWorkDayApplicable] bit NULL,
    [CalenderFlag] varchar(10) NULL,
    CONSTRAINT [PK_Act] PRIMARY KEY ([ID])
);
END
GO

IF OBJECT_ID('dbo.ComplianceCategory', 'U') IS NULL
BEGIN
CREATE TABLE [dbo].[ComplianceCategory] (
    [ID] int IDENTITY(1,1) NOT NULL,
    [Name] varchar(100) NOT NULL,
    [Description] varchar(MAX) NULL,
    [CreatedOn] datetime NULL,
    [UpdatedOn] datetime NULL,
    CONSTRAINT [PK_ComplianceCategory] PRIMARY KEY ([ID])
);
END
GO

IF OBJECT_ID('dbo.RecentComplianceTransactionView', 'V') IS NOT NULL DROP VIEW dbo.RecentComplianceTransactionView;
GO
----------------------------------------------------------------------------------
CREATE VIEW [dbo].[RecentComplianceTransactionView]  
AS  
SELECT        dbo.ComplianceTransaction.ID AS ComplianceTransactionID, dbo.ComplianceTransaction.ComplianceInstanceId, dbo.ComplianceTransaction.ComplianceScheduleOnID,   
                         dbo.ComplianceStatus.ID AS ComplianceStatusID, dbo.ComplianceStatus.Name AS Status, dbo.ComplianceTransaction.CreatedBy, dbo.ComplianceTransaction.Remarks, dbo.ComplianceTransaction.Dated,   
                         dbo.ComplianceTransaction.CreatedByText, dbo.ComplianceTransaction.StatusChangedOn, dbo.ComplianceTransaction.Penalty, dbo.ComplianceTransaction.Interest,dbo.ComplianceTransaction.IsPenaltySave,dbo.ComplianceTransaction.PenaltySubmit,   
                         dbo.ComplianceTransaction.ValuesAsPerSystem, dbo.ComplianceTransaction.ValuesAsPerReturn, dbo.ComplianceTransaction.LiabilityPaid,  
       dbo.ComplianceTransaction.FileNO,dbo.ComplianceTransaction.PhysicalLocation,  
       dbo.ComplianceTransaction.ChallanNo, dbo.ComplianceTransaction.Challanpaiddate, dbo.ComplianceTransaction.ChallanAmount, dbo.ComplianceTransaction.BankName, dbo.ComplianceTransaction.PerformBy, dbo.ComplianceTransaction.UpdatedOn
	   , dbo.ComplianceTransaction.DeviationReason, dbo.ComplianceTransaction.DeviationClosureDate
FROM            dbo.ComplianceTransaction INNER JOIN  
                             (SELECT        ComplianceScheduleOnID, MAX(Dated) AS Dated  
                               FROM            dbo.ComplianceTransaction AS ComplianceTransaction_1  
                               GROUP BY ComplianceScheduleOnID) AS RecentComplianceTransaction ON dbo.ComplianceTransaction.ComplianceScheduleOnID = RecentComplianceTransaction.ComplianceScheduleOnID AND   
                         dbo.ComplianceTransaction.Dated = RecentComplianceTransaction.Dated LEFT OUTER JOIN  
                         dbo.ComplianceStatus ON dbo.ComplianceStatus.ID = dbo.ComplianceTransaction.StatusId  
  

GO

-- Real ComplianceStatus dictionary rows + F-7's deliberate gap
-- Real ComplianceStatus rows 1-23 (matching InsightsStatusClassification's seed in sql/01), ASCII-safe text only.
SET IDENTITY_INSERT ComplianceStatus ON;
INSERT ComplianceStatus (ID, Name) VALUES (1, N'Status 1');
INSERT ComplianceStatus (ID, Name) VALUES (2, N'Status 2');
INSERT ComplianceStatus (ID, Name) VALUES (3, N'Status 3');
INSERT ComplianceStatus (ID, Name) VALUES (4, N'Status 4');
INSERT ComplianceStatus (ID, Name) VALUES (5, N'Status 5');
INSERT ComplianceStatus (ID, Name) VALUES (6, N'Status 6');
INSERT ComplianceStatus (ID, Name) VALUES (7, N'Status 7');
INSERT ComplianceStatus (ID, Name) VALUES (8, N'Status 8');
INSERT ComplianceStatus (ID, Name) VALUES (9, N'Status 9');
INSERT ComplianceStatus (ID, Name) VALUES (10, N'Status 10');
INSERT ComplianceStatus (ID, Name) VALUES (11, N'Status 11');
INSERT ComplianceStatus (ID, Name) VALUES (12, N'Status 12');
INSERT ComplianceStatus (ID, Name) VALUES (13, N'Status 13');
INSERT ComplianceStatus (ID, Name) VALUES (14, N'Status 14');
INSERT ComplianceStatus (ID, Name) VALUES (15, N'Status 15');
INSERT ComplianceStatus (ID, Name) VALUES (16, N'Status 16');
INSERT ComplianceStatus (ID, Name) VALUES (17, N'Status 17');
INSERT ComplianceStatus (ID, Name) VALUES (18, N'Status 18');
INSERT ComplianceStatus (ID, Name) VALUES (19, N'Status 19');
INSERT ComplianceStatus (ID, Name) VALUES (21, N'Status 21');
INSERT ComplianceStatus (ID, Name) VALUES (22, N'Status 22');
INSERT ComplianceStatus (ID, Name) VALUES (23, N'Status 23');
SET IDENTITY_INSERT ComplianceStatus OFF;

-- F-7 (docs/GOLDEN_FIXTURES.md): a status ID present in ComplianceStatus but absent from
-- InsightsStatusClassification. usp_Insights_AssertStatusCoverage scans ComplianceStatus
-- SYSTEM-WIDE, so this row makes EVERY dimension proc pre-flight THROW 51001 while it
-- exists - safe here (isolated CI container, no other tenant to disrupt), never safe
-- against a shared database (validated live via a rollback-wrapped transaction against
-- UAT instead - see the build report, nothing was ever committed there).
-- CI must: assert the THROW happens, DELETE this one row, then proceed (GOLDEN_FIXTURES.md
-- CI wiring step 3) - every other step in this file runs fine with it removed.
SET IDENTITY_INSERT ComplianceStatus ON;
INSERT ComplianceStatus (ID, Name) VALUES (99999, N'Golden Fixture F-7 Unmapped Status');
SET IDENTITY_INSERT ComplianceStatus OFF;

-- Real Act/Compliance master rows the fixture instances reference
SET IDENTITY_INSERT Act ON;
INSERT Act ([ID],[Name],[Description],[State],[City],[ComplianceTypeId],[ComplianceCategoryId],[CreatedOn],[IsDeleted],[StateID],[CityID],[CreatedBy],[UpdatedOn],[ProductCode],[StartDate],[Act_DeptID],[MinistryID],[RegulatorID],[duedatetype],[ActGroup],[ShortForm],[CountryID],[Actapplicabiltyrules],[LocationTypeID],[ActGroupId],[IsWorkDayApplicable],[CalenderFlag]) VALUES (5,N'Act Name fixture text',N'Act Description fixture text',N'Act State fixture text',N'Act City fixture text',2,12,'2014-08-14 04:41:25',0,NULL,NULL,1,'2020-02-06 16:10:37',N'C','2014-01-01 00:00:00',198,16,29,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
INSERT Act ([ID],[Name],[Description],[State],[City],[ComplianceTypeId],[ComplianceCategoryId],[CreatedOn],[IsDeleted],[StateID],[CityID],[CreatedBy],[UpdatedOn],[ProductCode],[StartDate],[Act_DeptID],[MinistryID],[RegulatorID],[duedatetype],[ActGroup],[ShortForm],[CountryID],[Actapplicabiltyrules],[LocationTypeID],[ActGroupId],[IsWorkDayApplicable],[CalenderFlag]) VALUES (32,N'Act Name fixture text',N'Act Description fixture text',N'Act State fixture text',N'Act City fixture text',2,15,'2014-08-14 04:41:26',0,NULL,NULL,1,'2020-02-06 16:10:45',N'C','2014-01-01 00:00:00',198,14,14,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
SET IDENTITY_INSERT Act OFF;
SET IDENTITY_INSERT Compliance ON;
INSERT Compliance ([ID],[Description],[UploadDocument],[Frequency],[DueDate],[RiskType],[NonComplianceType],[NonComplianceEffects],[CreatedOn],[IsDeleted],[ActID],[Sections],[ComplianceType],[FixedMinimum],[FixedMaximum],[VariableAmountPerDay],[VariableAmountPerDayMax],[Imprisonment],[Designation],[MinimumYears],[MaximumYears],[Others],[RequiredForms],[NatureOfCompliance],[VariableAmountPerMonth],[ShortDescription],[EventID],[EventComplianceType],[SubComplianceType],[FixedGap],[ReminderType],[ReminderBefore],[ReminderGap],[SubEventID],[CheckListTypeID],[OneTimeDate],[PenaltyDescription],[ReferenceMaterialText],[EffectiveDate],[EventFlag],[CreatedBy],[UpDocs],[ComplinceVisible],[Status],[DeactivateOn],[DeactivateDesc],[SampleFormLink],[ComplianceSubTypeID],[UpdatedOn],[DueWeekDay],[StartDate],[ShortForm],[duedatetype],[onlineoffline],[IsFrequencyBased],[TriggerNo],[triggerNoOfDays],[IntervalDays],[IsForcefulClosure],[MinimumPeriodType],[MaximumPeriodType],[VariableAmountPerInstance],[LocationTypeID],[IsForSecretarial],[VariableAmountPercent],[VariableAmountPercentMax],[ScheduleType],[IsMapped],[WorkingDays],[IsSatuarday],[ComplianceActionableProcedure],[WeekDay],[WeekNo],[ComplianceFormId],[IsSecretarialIntermediateCompliance],[DueTypeMode],[WeeklyDueDays]) VALUES (3,N'Compliance Description fixture text',1,2,30,1,0,N'Compliance NonComplianceEffects fixture text','2014-08-22 12:40:37',0,5,N'Compliance Sections fixture text',0,0,0,NULL,100,1,N'Compliance Designation fixture text',0,0,N'Compliance Others fixture text',N'Compliance RequiredForms fixture text',0,0,N'Compliance ShortDescription fixture text',NULL,NULL,NULL,NULL,0,NULL,NULL,NULL,NULL,NULL,N'Compliance PenaltyDescription fixture text',N'Compliance ReferenceMaterialText fixture text',NULL,NULL,1,NULL,1,NULL,NULL,N'Compliance DeactivateDesc fixture text',N'Compliance SampleFormLink fixture text',764,'2025-06-25 15:07:58',NULL,'2019-08-01 00:00:00',N'FSSAI - Licencing and Registration',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,N'0',N'0',NULL,1,NULL,NULL,N'Compliance ComplianceActionableProcedure fixture text',NULL,NULL,NULL,NULL,NULL,NULL);
INSERT Compliance ([ID],[Description],[UploadDocument],[Frequency],[DueDate],[RiskType],[NonComplianceType],[NonComplianceEffects],[CreatedOn],[IsDeleted],[ActID],[Sections],[ComplianceType],[FixedMinimum],[FixedMaximum],[VariableAmountPerDay],[VariableAmountPerDayMax],[Imprisonment],[Designation],[MinimumYears],[MaximumYears],[Others],[RequiredForms],[NatureOfCompliance],[VariableAmountPerMonth],[ShortDescription],[EventID],[EventComplianceType],[SubComplianceType],[FixedGap],[ReminderType],[ReminderBefore],[ReminderGap],[SubEventID],[CheckListTypeID],[OneTimeDate],[PenaltyDescription],[ReferenceMaterialText],[EffectiveDate],[EventFlag],[CreatedBy],[UpDocs],[ComplinceVisible],[Status],[DeactivateOn],[DeactivateDesc],[SampleFormLink],[ComplianceSubTypeID],[UpdatedOn],[DueWeekDay],[StartDate],[ShortForm],[duedatetype],[onlineoffline],[IsFrequencyBased],[TriggerNo],[triggerNoOfDays],[IntervalDays],[IsForcefulClosure],[MinimumPeriodType],[MaximumPeriodType],[VariableAmountPerInstance],[LocationTypeID],[IsForSecretarial],[VariableAmountPercent],[VariableAmountPercentMax],[ScheduleType],[IsMapped],[WorkingDays],[IsSatuarday],[ComplianceActionableProcedure],[WeekDay],[WeekNo],[ComplianceFormId],[IsSecretarialIntermediateCompliance],[DueTypeMode],[WeeklyDueDays]) VALUES (5,N'Compliance Description fixture text',1,3,30,0,2,N'Compliance NonComplianceEffects fixture text','2014-08-22 12:40:37',0,32,N'Compliance Sections fixture text',0,0,100000,NULL,5000,1,N'Compliance Designation fixture text',0,5,N'Compliance Others fixture text',N'Compliance RequiredForms fixture text',0,0,N'Compliance ShortDescription fixture text',NULL,NULL,NULL,NULL,0,NULL,NULL,NULL,NULL,NULL,N'Compliance PenaltyDescription fixture text',N'Compliance ReferenceMaterialText fixture text',NULL,NULL,1,NULL,1,NULL,NULL,N'Compliance DeactivateDesc fixture text',N'Compliance SampleFormLink fixture text',192,'2018-10-15 17:36:18',NULL,NULL,N'Env. Protection ',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,N'0',N'0',NULL,1,NULL,NULL,N'Compliance ComplianceActionableProcedure fixture text',NULL,NULL,NULL,NULL,NULL,NULL);
SET IDENTITY_INSERT Compliance OFF;

-- Tenant 999001 (F-1..F-13) and tenant 999002 (F-14) fixture data
-- Golden fixture build for tenant 999001 (docs/GOLDEN_FIXTURES.md F-1..F-13) and
-- tenant 999002 (F-14, kept separate - see report). ADDITIVE ONLY: every INSERT
-- below targets CustomerID 999001/999002 or references EXISTING master rows
-- (Compliance 3/5, Act 5/32) read-only. Nothing outside those two CustomerIDs
-- is ever written.

SET NOCOUNT ON;

DECLARE @ComplianceIdPrimary INT = 3;   -- Act 5, category 12
DECLARE @ComplianceIdSecondary INT = 5; -- Act 32, category 15
DECLARE @Now DATETIME = GETDATE();
DECLARE @Past DATETIME = DATEADD(DAY, -10, @Now);
DECLARE @Future DATETIME = DATEADD(DAY, 30, @Now);
DECLARE @StateID INT = 5;
DECLARE @CityID INT = 179;
DECLARE @BranchType TINYINT = 1;

BEGIN TRY
BEGIN TRANSACTION;

------------------------------------------------------------------
-- TENANT 999001
------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM Customer WHERE ID = 999001)
BEGIN
    RAISERROR('Tenant 999001 already exists - aborting, this script is meant to run once.', 16, 1);
END

SET IDENTITY_INSERT Customer ON;
INSERT Customer (ID, Name, Address, BuyerName, BuyerContactNumber, CreatedOn, IsDeleted, IComplianceApplicable)
VALUES (999001, N'Golden Fixture Tenant (999001)', N'N/A', N'N/A', N'N/A', @Now, 0, 1);
SET IDENTITY_INSERT Customer OFF;

-- Branches
DECLARE @ApexId INT, @Main1Id INT, @Main2Id INT, @IntermediateId INT,
        @LeafAId INT, @LeafBId INT, @GhostId INT, @DeletedBranchId INT,
        @OrphanParentId INT, @OrphanChildId INT;

INSERT CustomerBranch (Name, CustomerID, IsDeleted, CreatedOn, ParentID, Type, StateID, CityID, Status)
VALUES (N'Golden Apex', 999001, 0, @Now, NULL, @BranchType, @StateID, @CityID, 1);
SET @ApexId = SCOPE_IDENTITY();

INSERT CustomerBranch (Name, CustomerID, IsDeleted, CreatedOn, ParentID, Type, StateID, CityID, Status)
VALUES (N'Golden Main 1', 999001, 0, @Now, @ApexId, @BranchType, @StateID, @CityID, 1);
SET @Main1Id = SCOPE_IDENTITY();

INSERT CustomerBranch (Name, CustomerID, IsDeleted, CreatedOn, ParentID, Type, StateID, CityID, Status)
VALUES (N'Golden Main 2', 999001, 0, @Now, @ApexId, @BranchType, @StateID, @CityID, 1);
SET @Main2Id = SCOPE_IDENTITY();

INSERT CustomerBranch (Name, CustomerID, IsDeleted, CreatedOn, ParentID, Type, StateID, CityID, Status)
VALUES (N'Golden Intermediate', 999001, 0, @Now, @ApexId, @BranchType, @StateID, @CityID, 1);
SET @IntermediateId = SCOPE_IDENTITY();

INSERT CustomerBranch (Name, CustomerID, IsDeleted, CreatedOn, ParentID, Type, StateID, CityID, Status)
VALUES (N'Golden Leaf A', 999001, 0, @Now, @IntermediateId, @BranchType, @StateID, @CityID, 1);
SET @LeafAId = SCOPE_IDENTITY();

INSERT CustomerBranch (Name, CustomerID, IsDeleted, CreatedOn, ParentID, Type, StateID, CityID, Status)
VALUES (N'Golden Leaf B', 999001, 0, @Now, @IntermediateId, @BranchType, @StateID, @CityID, 1);
SET @LeafBId = SCOPE_IDENTITY();

INSERT CustomerBranch (Name, CustomerID, IsDeleted, CreatedOn, ParentID, Type, StateID, CityID, Status)
VALUES (N'Golden Ghost Leaf', 999001, 0, @Now, @ApexId, @BranchType, @StateID, @CityID, 1);
SET @GhostId = SCOPE_IDENTITY();

INSERT CustomerBranch (Name, CustomerID, IsDeleted, CreatedOn, ParentID, Type, StateID, CityID, Status)
VALUES (N'Golden Deleted Branch', 999001, 1, @Now, @ApexId, @BranchType, @StateID, @CityID, 1);
SET @DeletedBranchId = SCOPE_IDENTITY();

INSERT CustomerBranch (Name, CustomerID, IsDeleted, CreatedOn, ParentID, Type, StateID, CityID, Status)
VALUES (N'Golden Orphan Parent (deleted)', 999001, 1, @Now, NULL, @BranchType, @StateID, @CityID, 1);
SET @OrphanParentId = SCOPE_IDENTITY();

INSERT CustomerBranch (Name, CustomerID, IsDeleted, CreatedOn, ParentID, Type, StateID, CityID, Status)
VALUES (N'Golden Orphan Child', 999001, 0, @Now, @OrphanParentId, @BranchType, @StateID, @CityID, 1);
SET @OrphanChildId = SCOPE_IDENTITY();

DECLARE @i INT, @NewInstanceId INT, @NewScheduleId INT;

-- F-1: 10 past-due, status 7 (completed on-time) - Main1
SET @i = 0;
WHILE @i < 10
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @Main1Id, 1, @Past);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Past, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 7, @NewScheduleId, @Past, N'golden fixture F-1', 1);
    SET @i = @i + 1;
END

-- F-2: 10 past-due, status 9 (completed late) - Main1
SET @i = 0;
WHILE @i < 10
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @Main1Id, 1, @Past);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Past, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 9, @NewScheduleId, @Past, N'golden fixture F-2', 1);
    SET @i = @i + 1;
END

-- F-3: 10 past-due, status 2 (pending review, overdue-eligible) - Main1
SET @i = 0;
WHILE @i < 10
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @Main1Id, 1, @Past);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Past, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 2, @NewScheduleId, @Past, N'golden fixture F-3', 1);
    SET @i = @i + 1;
END

-- F-4a: 10 past-due, status 15 (reviewer-final NA) - Main2
SET @i = 0;
WHILE @i < 10
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @Main2Id, 1, @Past);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Past, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 15, @NewScheduleId, @Past, N'golden fixture F-4a', 1);
    SET @i = @i + 1;
END

-- F-4b: 10 past-due, status 17 (reviewer-final not-complied) - Main2
SET @i = 0;
WHILE @i < 10
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @Main2Id, 1, @Past);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Past, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 17, @NewScheduleId, @Past, N'golden fixture F-4b', 1);
    SET @i = @i + 1;
END

-- F-5a: 10 past-due, status 18 (performer-proposed NA, still open) - Main2
SET @i = 0;
WHILE @i < 10
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @Main2Id, 1, @Past);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Past, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 18, @NewScheduleId, @Past, N'golden fixture F-5a', 1);
    SET @i = @i + 1;
END

-- F-5b: 10 past-due, status 16 (performer-proposed not-complied, still open) - Main2
SET @i = 0;
WHILE @i < 10
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @Main2Id, 1, @Past);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Past, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 16, @NewScheduleId, @Past, N'golden fixture F-5b', 1);
    SET @i = @i + 1;
END

-- F-6 config: 10 past-due, status 1 (open, overdue-eligible) directly on the INTERMEDIATE node
SET @i = 0;
WHILE @i < 10
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @IntermediateId, 1, @Past);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Past, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 1, @NewScheduleId, @Past, N'golden fixture F-6 intermediate', 1);
    SET @i = @i + 1;
END

-- F-6 leaf A: 10 instances, FUTURE-scheduled (rollup existence only, must not affect past-due totals)
SET @i = 0;
WHILE @i < 10
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @LeafAId, 1, @Future);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Future, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 1, @NewScheduleId, @Now, N'golden fixture F-6 leaf A', 1);
    SET @i = @i + 1;
END

-- F-6 leaf B: 5 instances, FUTURE-scheduled
SET @i = 0;
WHILE @i < 5
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @LeafBId, 1, @Future);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Future, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 1, @NewScheduleId, @Now, N'golden fixture F-6 leaf B', 1);
    SET @i = @i + 1;
END

-- F-9: 1 instance on the SOFT-DELETED branch - must vanish from every cut
INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
VALUES (@ComplianceIdPrimary, @Now, 0, @DeletedBranchId, 1, @Past);
SET @NewInstanceId = SCOPE_IDENTITY();
INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
VALUES (@NewInstanceId, @Past, 1, 1);
SET @NewScheduleId = SCOPE_IDENTITY();
INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
VALUES (@NewInstanceId, 1, @NewScheduleId, @Past, N'golden fixture F-9', 1);

-- F-11: 20 instances on the active ORPHAN CHILD branch, FUTURE-scheduled (rollup existence test)
SET @i = 0;
WHILE @i < 20
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @OrphanChildId, 1, @Future);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Future, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 1, @NewScheduleId, @Now, N'golden fixture F-11', 1);
    SET @i = @i + 1;
END

-- F-8 negative-scope material: 2 instances on Main1, SECONDARY category, FUTURE-scheduled
SET @i = 0;
WHILE @i < 2
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdSecondary, @Now, 0, @Main1Id, 1, @Future);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Future, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 1, @NewScheduleId, @Now, N'golden fixture F-8 negative', 1);
    SET @i = @i + 1;
END

-- F-8: an ACTIVE user scoped ONLY to (Main1, primary category)
DECLARE @ScopedUserId INT;
INSERT [User] (FirstName, LastName, CreatedByText, CreatedOn, IsDeleted, IsActive, CustomerID)
VALUES (N'Golden', N'ScopedUser', N'golden fixture', @Now, 0, 1, 999001);
SET @ScopedUserId = SCOPE_IDENTITY();
INSERT EntitiesAssignment (UserID, BranchID, ComplianceCatagoryID, CreatedOn)
VALUES (@ScopedUserId, @Main1Id, 12, @Now);

-- F-10: a DEACTIVATED user (IsActive=0, IsDeleted=0) holding 5 live EntitiesAssignment rows
DECLARE @DeactivatedUserId INT;
INSERT [User] (FirstName, LastName, CreatedByText, CreatedOn, IsDeleted, IsActive, CustomerID)
VALUES (N'Golden', N'DeactivatedUser', N'golden fixture', @Now, 0, 0, 999001);
SET @DeactivatedUserId = SCOPE_IDENTITY();
INSERT EntitiesAssignment (UserID, BranchID, ComplianceCatagoryID, CreatedOn) VALUES (@DeactivatedUserId, @Main1Id, 12, @Now);
INSERT EntitiesAssignment (UserID, BranchID, ComplianceCatagoryID, CreatedOn) VALUES (@DeactivatedUserId, @Main2Id, 12, @Now);
INSERT EntitiesAssignment (UserID, BranchID, ComplianceCatagoryID, CreatedOn) VALUES (@DeactivatedUserId, @IntermediateId, 12, @Now);
INSERT EntitiesAssignment (UserID, BranchID, ComplianceCatagoryID, CreatedOn) VALUES (@DeactivatedUserId, @LeafAId, 12, @Now);
INSERT EntitiesAssignment (UserID, BranchID, ComplianceCatagoryID, CreatedOn) VALUES (@DeactivatedUserId, @LeafBId, 12, @Now);

-- Tenant-wide scoped user (all branches x both categories) - needed to exercise
-- usp_Insights_Dimension_Location at full tenant scope (the F-8/F-10 users above are
-- deliberately narrow-scoped and would not see most of the fixture).
DECLARE @TenantWideUserId999001 INT;
INSERT [User] (FirstName, LastName, CreatedByText, CreatedOn, IsDeleted, IsActive, CustomerID)
VALUES (N'Golden', N'TenantWideUser', N'golden fixture', @Now, 0, 1, 999001);
SET @TenantWideUserId999001 = SCOPE_IDENTITY();
INSERT EntitiesAssignment (UserID, BranchID, ComplianceCatagoryID, CreatedOn)
SELECT @TenantWideUserId999001, cb.ID, cat.CategoryId, @Now
FROM CustomerBranch cb
CROSS JOIN (SELECT 12 AS CategoryId UNION ALL SELECT 15) cat
WHERE cb.CustomerID = 999001 AND cb.IsDeleted = 0;

------------------------------------------------------------------
-- TENANT 999002 (F-14: single-branch tenant, comparatives suppressed)
------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM Customer WHERE ID = 999002)
BEGIN
    RAISERROR('Tenant 999002 already exists - aborting.', 16, 1);
END

SET IDENTITY_INSERT Customer ON;
INSERT Customer (ID, Name, Address, BuyerName, BuyerContactNumber, CreatedOn, IsDeleted, IComplianceApplicable)
VALUES (999002, N'Golden Fixture Tenant Single-Branch (999002)', N'N/A', N'N/A', N'N/A', @Now, 0, 1);
SET IDENTITY_INSERT Customer OFF;

DECLARE @SingleBranchId INT;
INSERT CustomerBranch (Name, CustomerID, IsDeleted, CreatedOn, ParentID, Type, StateID, CityID, Status)
VALUES (N'Golden Only Branch', 999002, 0, @Now, NULL, @BranchType, @StateID, @CityID, 1);
SET @SingleBranchId = SCOPE_IDENTITY();

SET @i = 0;
WHILE @i < 15
BEGIN
    INSERT ComplianceInstance (ComplianceId, CreatedOn, IsDeleted, CustomerBranchID, GenerateSchedule, ScheduledOn)
    VALUES (@ComplianceIdPrimary, @Now, 0, @SingleBranchId, 1, @Past);
    SET @NewInstanceId = SCOPE_IDENTITY();
    INSERT ComplianceScheduleOn (ComplianceInstanceID, ScheduleOn, IsActive, IsUpcomingNotDeleted)
    VALUES (@NewInstanceId, @Past, 1, 1);
    SET @NewScheduleId = SCOPE_IDENTITY();
    INSERT ComplianceTransaction (ComplianceInstanceId, StatusId, ComplianceScheduleOnID, Dated, Remarks, CreatedBy)
    VALUES (@NewInstanceId, 1, @NewScheduleId, @Past, N'golden fixture F-14', 1);
    SET @i = @i + 1;
END

DECLARE @TenantWideUserId999002 INT;
INSERT [User] (FirstName, LastName, CreatedByText, CreatedOn, IsDeleted, IsActive, CustomerID)
VALUES (N'Golden', N'TenantWideUser', N'golden fixture', @Now, 0, 1, 999002);
SET @TenantWideUserId999002 = SCOPE_IDENTITY();
INSERT EntitiesAssignment (UserID, BranchID, ComplianceCatagoryID, CreatedOn)
VALUES (@TenantWideUserId999002, @SingleBranchId, 12, @Now);

COMMIT TRANSACTION;
PRINT 'Golden fixture build for 999001/999002 committed.';
PRINT CONCAT('ApexId=', @ApexId, ' Main1Id=', @Main1Id, ' Main2Id=', @Main2Id,
             ' IntermediateId=', @IntermediateId, ' LeafAId=', @LeafAId, ' LeafBId=', @LeafBId,
             ' GhostId=', @GhostId, ' DeletedBranchId=', @DeletedBranchId,
             ' OrphanParentId=', @OrphanParentId, ' OrphanChildId=', @OrphanChildId,
             ' SingleBranchId=', @SingleBranchId,
             ' ScopedUserId=', @ScopedUserId, ' DeactivatedUserId=', @DeactivatedUserId);
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @ErrMsg NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrLine INT = ERROR_LINE();
    RAISERROR('Golden fixture build FAILED and was rolled back. Line %d: %s', 16, 1, @ErrLine, @ErrMsg);
END CATCH
