using Microsoft.EntityFrameworkCore;

namespace Insights.Data;

/// <summary>
/// Serializes GeneratedReport writes for one tenant across every worker replica - the fix for a
/// real, twice-live-found bug (PersistActivity.cs's own doc comment): concurrent PersistActivity
/// calls for the SAME tenant hitting SaveChangesAsync at once fail with EF's generic
/// "An error occurred while saving the entity changes." First found 2026-09-18 (4 replicas racing
/// one report), found again 2026-09-24 (one reqId's sibling dimensions finishing together) - same
/// mechanism both times, only the trigger differed.
///
/// Deliberately NOT a .NET-side lock (SemaphoreSlim, a static dictionary, anything in-process):
/// separate worker pods do not share memory, and the 2026-09-18 incident was explicitly cross-pod.
/// The only thing every replica actually shares is the database itself, so real implementations of
/// this interface must enforce the lock there (SQL Server's sp_getapplock, in
/// <see cref="Insights.Worker.Orchestration.Activities.SqlTenantReportLock"/>).
///
/// Abstracted behind an interface, not inlined in PersistActivity, because the real implementation
/// needs a relational transaction (sp_getapplock's LockOwner='Transaction' has no meaning without
/// one) and EF Core's InMemory test provider does not support transactions at all - a hard
/// requirement to keep PersistActivityTests fast and DB-behaviour-light, the same reason
/// IReportEncryptor/IReportBlobWriter are already interfaces rather than concrete classes.
/// </summary>
public interface ITenantReportLock
{
    /// <summary>
    /// Runs <paramref name="action"/> with this tenant's write lock held. A real implementation
    /// must guarantee no two callers for the SAME tenantId run <paramref name="action"/>
    /// concurrently against the same database, across every process sharing that database -
    /// callers for DIFFERENT tenants must never block each other.
    /// </summary>
    Task<T> ExecuteWithLockAsync<T>(DbContext db, int tenantId, Func<Task<T>> action);
}
