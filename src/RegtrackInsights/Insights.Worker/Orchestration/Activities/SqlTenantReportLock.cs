using Insights.Data;
using Microsoft.EntityFrameworkCore;

namespace Insights.Worker.Orchestration.Activities;

/// <summary>
/// Real implementation of <see cref="ITenantReportLock"/> - see that interface's doc comment for
/// why this has to be a database-enforced lock, not an in-process one.
///
/// sp_getapplock, LockOwner='Transaction': acquired inside a transaction this class opens, released
/// automatically on commit OR rollback - no separate release call to forget, and no risk of holding
/// the lock past the caller's own connection lifetime. Wrapped in the DbContext's own execution
/// strategy (EnableRetryOnFailure, WorkerRegistration.cs) via CreateExecutionStrategy().ExecuteAsync
/// - the officially-documented way to combine EF Core's automatic retry with an explicit
/// transaction; a bare manual BeginTransactionAsync outside this wrapper is what EF Core explicitly
/// disallows once retry-on-failure is configured. The whole delegate, including
/// <paramref name="action"/>, re-runs on a transient retry - safe only because the caller's own
/// action re-checks for an already-committed row before writing (see PersistActivity.RunAsync).
/// </summary>
public sealed class SqlTenantReportLock : ITenantReportLock
{
    public async Task<T> ExecuteWithLockAsync<T>(DbContext db, int tenantId, Func<Task<T>> action)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();

            var lockReturnCode = await AcquireTenantAppLockAsync(db, tenantId);
            if (lockReturnCode < 0)
                throw new InvalidOperationException(
                    $"SqlTenantReportLock: sp_getapplock for tenant {tenantId} returned {lockReturnCode} (timeout, cancelled, or deadlock victim) - could not serialize concurrent report writes.");

            var result = await action();
            await transaction.CommitAsync();
            return result;
        });
    }

    /// <summary>
    /// Return codes per Microsoft's own documented contract for sp_getapplock: 0 or 1 = acquired
    /// (1 means acquired after waiting on another owner - both are success), &lt; 0 = timeout (-1),
    /// cancelled (-2), deadlock victim (-3), or a parameter error (-999).
    /// </summary>
    private static async Task<int> AcquireTenantAppLockAsync(DbContext db, int tenantId)
    {
        var resource = $"GeneratedReport:{tenantId}";
        var rows = await db.Database.SqlQuery<int>(
            $@"DECLARE @lockResult int;
               EXEC @lockResult = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
               SELECT @lockResult AS Value;").ToListAsync();
        return rows[0];
    }
}
