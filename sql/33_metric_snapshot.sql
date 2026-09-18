/*===========================================================================
  RegTrack Insights - Phase 1d
  METRIC SNAPSHOT - the only way this engine will ever answer "since when?"

  Error block : 51042-51049 (see note below - NOT 51050-51059, which is
  already allocated to sql/07 entity, and NOT 51040-51041, already in real
  live use - see the note below).

  -- WHY THIS EXISTS ------------------------------------------------------
  Every dimension answers "what is true now". Not one can answer "is this
  getting worse", because the database holds only the present. A CCO shown
  "Import-Export is 79.7% overdue" asks "since when?" and the engine has
  nothing.

  This cannot be backfilled. The history starts the day this table starts
  being written, so the cheapest useful thing is to start now and let it
  accumulate while the rest of the work happens.

  -- WHAT GOES IN --------------------------------------------------------
  ONLY control-total scalars - the reconciled headline figures each dimension
  already emits in result set 1. Never per-row detail: this is a trend spine,
  not a warehouse. A dimension with 1,709 branch rows contributes perhaps a
  dozen numbers.

  -- WHAT MUST NOT HAPPEN ------------------------------------------------
  [TRAP] Overdue is a FLOW metric. Two snapshots of a flow metric taken at
  different times are NOT a like-for-like comparison unless both were taken
  at the same point in the compliance cycle. Weekly-on-the-same-weekday is
  the minimum discipline; anything ad-hoc will produce sawtooth noise that
  reads as a trend. IsComparable carries that judgement per row rather than
  leaving the reader to guess.

  -- [RECONCILED 2026-09-18] This object set (table + 3 procs) was already
  deployed live to UAT (confirmed via sys.objects/sys.sql_modules) before it
  had ever been merged into this repo - handed off as a file numbered 28,
  which collides with this repo's OWN sql/28 (generated_report_dimension_key,
  also already live, also predating this handoff). Renumbered 33 here, the
  free slot on THIS repo's sequence - the opposite direction from the
  handoff's own renumbering, because the two environments' number 28 was
  already taken by two DIFFERENT objects independently.

  The handoff's original file also claimed error block 51050-51059 was "from
  the free list" per CLAUDE.md Sec.5b. It is not: that block belongs to
  sql/07 (entity), and the deployed procs below genuinely threw 51050/51051 -
  the exact same numbers sql/07's live proc throws for two unrelated real
  conditions (scope denied; reconciliation failed). Confirmed live via
  sys.sql_modules on both procs before this fix. Renumbered to 51042/51043
  here - also avoiding 51040/51041, which sql/07's live proc uses too (a
  deliberate cross-dimension SHARED code for a Flagged>Eligible guard, not
  tracked in this repo's own sql/07 file - a separate, still-open drift).
  The live SnapshotRecord/SnapshotPurge procs need an ALTER PROCEDURE to
  actually pick up 51042/51043 - this file alone does not change them,
  since both already exist and this script's CREATE is a no-op against a
  live object of the same name and definition-mismatch.

  ADDITIVE. One table, three procedures. Touches nothing that exists.
===========================================================================*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.InsightsMetricSnapshot', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InsightsMetricSnapshot (
        SnapshotID    BIGINT IDENTITY(1,1) NOT NULL,
        CustomerID    INT           NOT NULL,
        /*  Date, not datetime, and the same "two runs in the same week must
            collide" reasoning as InsightsFreeDigestLog.WeekEnding (sql/15).   */
        AsOfDate      DATE          NOT NULL,
        Dimension     VARCHAR(40)   NOT NULL,   -- 'Location', 'Departments', ...
        Metric        VARCHAR(60)   NOT NULL,   -- the control_totals column name, verbatim
        Value         DECIMAL(18,4) NULL,       -- NULL is meaningful: not computable this run
        /*  A flow metric compared across runs taken at different points in the
            cycle is noise. The WRITER decides, because only the caller knows
            whether this run is the scheduled weekly one or an ad-hoc rerun.   */
        IsComparable  BIT           NOT NULL CONSTRAINT DF_IMS_Comparable DEFAULT (1),
        MetricClass   VARCHAR(10)   NOT NULL,   -- 'stock' | 'flow' | 'rate'
        CapturedUtc   DATETIME2(0)  NOT NULL CONSTRAINT DF_IMS_Captured DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_InsightsMetricSnapshot PRIMARY KEY (SnapshotID),
        /*  One value per tenant/date/dimension/metric. A rerun on the same day
            UPDATES rather than appending a second, contradictory row.          */
        CONSTRAINT UQ_InsightsMetricSnapshot UNIQUE (CustomerID, AsOfDate, Dimension, Metric)
    );

    CREATE INDEX IX_IMS_Trend ON dbo.InsightsMetricSnapshot (CustomerID, Dimension, Metric, AsOfDate)
        INCLUDE (Value, IsComparable, MetricClass);
END
GO

/*---------------------------------------------------------------------------
  RECORD - upsert one metric. Idempotent on (tenant, date, dimension, metric).
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_SnapshotRecord', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_SnapshotRecord;
GO
CREATE PROCEDURE dbo.usp_Insights_SnapshotRecord
    @CustomerID   INT,
    @AsOfDate     DATE,
    @Dimension    VARCHAR(40),
    @Metric       VARCHAR(60),
    @Value        DECIMAL(18,4),
    @MetricClass  VARCHAR(10),
    @IsComparable BIT = 1
AS
BEGIN
    SET NOCOUNT ON;

    IF @MetricClass NOT IN ('stock', 'flow', 'rate')
        THROW 51042, N'METRIC SNAPSHOT - MetricClass must be stock, flow or rate. A metric whose class is unknown cannot be trended safely.', 1;

    UPDATE dbo.InsightsMetricSnapshot
       SET Value = @Value, IsComparable = @IsComparable,
           MetricClass = @MetricClass, CapturedUtc = SYSUTCDATETIME()
     WHERE CustomerID = @CustomerID AND AsOfDate = @AsOfDate
       AND Dimension  = @Dimension  AND Metric   = @Metric;

    IF @@ROWCOUNT = 0
        INSERT dbo.InsightsMetricSnapshot (CustomerID, AsOfDate, Dimension, Metric, Value, IsComparable, MetricClass)
        VALUES (@CustomerID, @AsOfDate, @Dimension, @Metric, @Value, @IsComparable, @MetricClass);
END
GO

/*---------------------------------------------------------------------------
  TREND - the read surface. Returns the current value beside the closest
  earlier COMPARABLE snapshot, with the gap in days so the caller can judge
  whether the comparison is worth making.

  Emits nothing rather than a fabricated zero when there is no history. A
  dimension with one snapshot has no trend, and saying so is the honest
  answer - "0% change" would be a lie.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_SnapshotTrend', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_SnapshotTrend;
GO
CREATE PROCEDURE dbo.usp_Insights_SnapshotTrend
    @CustomerID    INT,
    @AsOfDate      DATE,
    @LookbackDays  INT = 90,
    @MinGapDays    INT = 21          -- two snapshots a day apart are not a trend
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        cur.Dimension,
        cur.Metric,
        cur.MetricClass,
        cur.Value                              AS CurrentValue,
        prv.Value                              AS PriorValue,
        prv.AsOfDate                           AS PriorAsOfDate,
        DATEDIFF(DAY, prv.AsOfDate, cur.AsOfDate) AS GapDays,
        CASE WHEN prv.Value IS NULL THEN NULL
             ELSE cur.Value - prv.Value END    AS AbsoluteChange,
        CASE WHEN prv.Value IS NULL OR prv.Value = 0 THEN NULL
             ELSE CAST(100.0 * (cur.Value - prv.Value) / ABS(prv.Value) AS DECIMAL(9,1)) END AS PctChange,
        CASE WHEN prv.Value IS NULL           THEN 'no_comparable_history'
             WHEN cur.Value  = prv.Value      THEN 'flat'
             WHEN cur.Value  > prv.Value      THEN 'up'
             ELSE 'down' END                   AS Direction,
        /*  Direction is NOT quality. Overdue up is bad; on-time up is good.
            This proc does not know which, and must not guess - the caller
            reads Direction against the metric's own polarity.               */
        CAST(0 AS BIT)                         AS DirectionImpliesQuality
    FROM dbo.InsightsMetricSnapshot cur
    OUTER APPLY (
        SELECT TOP 1 p.Value, p.AsOfDate
        FROM dbo.InsightsMetricSnapshot p
        WHERE p.CustomerID   = cur.CustomerID
          AND p.Dimension    = cur.Dimension
          AND p.Metric       = cur.Metric
          AND p.AsOfDate     < cur.AsOfDate
          AND p.AsOfDate    >= DATEADD(DAY, -@LookbackDays, cur.AsOfDate)
          AND DATEDIFF(DAY, p.AsOfDate, cur.AsOfDate) >= @MinGapDays
          AND p.IsComparable = 1
        ORDER BY p.AsOfDate DESC) prv
    WHERE cur.CustomerID   = @CustomerID
      AND cur.AsOfDate     = @AsOfDate
      AND cur.IsComparable = 1
    ORDER BY cur.Dimension, cur.Metric;
END
GO

/*---------------------------------------------------------------------------
  PURGE - retention is an ops setting, never a literal here.
---------------------------------------------------------------------------*/
IF OBJECT_ID('dbo.usp_Insights_SnapshotPurge', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_Insights_SnapshotPurge;
GO
CREATE PROCEDURE dbo.usp_Insights_SnapshotPurge
    @RetentionDays INT
AS
BEGIN
    SET NOCOUNT ON;

    IF @RetentionDays < 400
        THROW 51043, N'METRIC SNAPSHOT - retention below 400 days would destroy the year-over-year comparison this table exists to enable. Raise the retention or change this guard deliberately.', 1;

    DELETE FROM dbo.InsightsMetricSnapshot
    WHERE AsOfDate < DATEADD(DAY, -@RetentionDays, CAST(SYSUTCDATETIME() AS DATE));

    SELECT @@ROWCOUNT AS RowsPurged;
END
GO

PRINT 'Metric snapshot store installed.';
GO
