using Insights.Domain;
using Microsoft.EntityFrameworkCore;

namespace Insights.Data;

/// <summary>
/// EF Core, deliberately - the sole exception to this repo's "Dapper for the read path" rule
/// (CLAUDE.md 7: "EF Core only for the GeneratedReport index row"). Plain CRUD against one table,
/// no scope/entitlement/reconciliation logic for SQL to enforce, so a proc layer adds nothing here.
/// The table itself is created by sql/18_generated_report.sql, deployed via sqlcmd like every
/// other table in this repo - NOT by EF migrations, which this project does not use.
/// </summary>
public sealed class InsightsReportsDbContext(DbContextOptions<InsightsReportsDbContext> options) : DbContext(options)
{
    public DbSet<GeneratedReport> GeneratedReports => Set<GeneratedReport>();

    /// <summary>sql/30_report_request.sql - the fan-out reqId -> runId grouping (see IReportRequestRepository).</summary>
    public DbSet<ReportRequestUnit> ReportRequestUnits => Set<ReportRequestUnit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GeneratedReport>(entity =>
        {
            entity.ToTable("GeneratedReport");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasDefaultValueSql("NEWID()");
            entity.Property(r => r.GeneratedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(r => r.KeyVaultObjectSalt).HasDefaultValue("0");
        });

        modelBuilder.Entity<ReportRequestUnit>(entity =>
        {
            entity.ToTable("InsightsReportRequest");
            entity.HasKey(u => new { u.ReqId, u.RunId });
            entity.Property(u => u.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
        });
    }
}
