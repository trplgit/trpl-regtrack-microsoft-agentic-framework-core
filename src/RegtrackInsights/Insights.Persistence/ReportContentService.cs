using Insights.Data;
using Insights.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Insights.Persistence;

/// <summary>
/// Item 14's read half. Server sequence exactly matches API_CONTRACTS.md §5:
///   1. (caller does tenant eligibility before calling this - see ReportContentEndpoints)
///   2. Re-resolve scope NOW; enforce report_scope subset-of viewer_scope, else refuse
///   3. Decrypt via the DocAI envelope pattern
///   4. Mint a short-lived view location (IReportViewPublisher)
///   5. Write an access-audit log line
///
/// [KNOWN LIMITATION, flagged not hidden] GeneratedReport.ScopeDescriptor is a LABEL ("tenant" or
/// "entity:{id}"), not a snapshot of the actual (BranchID, CategoryID) pairs the report was
/// generated against - nothing persists that snapshot today. So the subset check below is
/// necessarily an APPROXIMATION on the branch axis and cannot check the category axis at all:
///   - "tenant" reports: refuse only if the viewer's CURRENT scope is now completely empty for
///     this tenant. Catches full revocation (design doc D4's actual scenario). Does NOT catch a
///     functional user's category access narrowing (9 categories down to 5, say) - the descriptor
///     does not distinguish tenant_wide from functional, so there is nothing finer to check against.
///   - "entity:{id}" reports: refuse unless the viewer's CURRENT branches cover the WHOLE subtree
///     under that entity - precise on the branch axis (real subtree walk via IEntityRepository).
///     Category axis still unchecked, same reason.
/// Real fix: persist the actual scope-pair set (or at minimum ScopeClassification) on GeneratedReport
/// at generation time, and diff against it here instead of re-deriving from a label. Not done in
/// this slice - flagged so it is a visible decision, not a silent gap.
/// </summary>
public sealed class ReportContentService(
    InsightsReportsDbContext db,
    IScopeRepository scope,
    IEntityRepository entities,
    IReportDecryptor decryptor,
    IReportBlobReader blobReader,
    IReportViewPublisher viewPublisher,
    TimeSpan sasLifetime,
    ILogger<ReportContentService> logger) : IReportContentService
{
    public async Task<ReportContentResult?> OpenAsync(
        Guid reportId, int tenantId, int viewerUserId, CancellationToken cancellationToken = default)
    {
        var report = await db.GeneratedReports
            .Where(r => r.Id == reportId && r.CustomerId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (report is null)
        {
            logger.LogInformation("Report {ReportId} view refused for user {UserId}: no such report on tenant {TenantId}.", reportId, viewerUserId, tenantId);
            return null;
        }

        var viewerPairs = await scope.GetScopePairsAsync(viewerUserId, tenantId, cancellationToken);
        if (!await CoversReportScopeAsync(report.ScopeDescriptor, tenantId, viewerPairs, cancellationToken))
        {
            logger.LogInformation(
                "Report {ReportId} view refused for user {UserId}: current scope no longer covers report_scope {ScopeDescriptor}.",
                reportId, viewerUserId, report.ScopeDescriptor);
            return null;
        }

        var encryptedContent = await blobReader.ReadAsync(new BlobLocation(report.BlobContainer, report.BlobPath), cancellationToken);
        var plaintextHtml = await decryptor.DecryptAsync(encryptedContent, report.EncryptedAesKey, report.KeyVaultObjectVersion, cancellationToken);

        var location = await viewPublisher.PublishAsync(plaintextHtml, sasLifetime, cancellationToken);

        // sql/19 - stamped only on a SUCCESSFUL view (past the scope re-check, decrypt and
        // publish above), never on a refusal. This is the paid keep-warm scheduler's sole signal
        // that a report was actually opened (design doc Sec.4.3) - report is EF-tracked from the
        // query above, so the mutation plus SaveChanges is the whole write.
        report.LastViewedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // Step 5: access-audit row. A structured log line for this slice - real DB audit table
        // (queryable, retained, purgeable alongside the rest of a tenant's data) is a fast-follow,
        // not built here. Flagged, not silently treated as done.
        logger.LogInformation(
            "Report {ReportId} (tenant {TenantId}, scope {ScopeDescriptor}) opened by user {UserId} at {OpenedAtUtc}.",
            reportId, tenantId, report.ScopeDescriptor, viewerUserId, DateTimeOffset.UtcNow);

        return new ReportContentResult(location.ContentUrl, location.ExpiresUtc, SandboxRequired: true);
    }

    private async Task<bool> CoversReportScopeAsync(
        string scopeDescriptor, int tenantId, IReadOnlyList<ScopePair> viewerPairs, CancellationToken cancellationToken)
    {
        if (scopeDescriptor == "tenant")
            return viewerPairs.Count > 0;

        if (!scopeDescriptor.StartsWith("entity:", StringComparison.Ordinal) ||
            !int.TryParse(scopeDescriptor.AsSpan("entity:".Length), out var entityId))
            throw new InvalidOperationException($"Unrecognised report scope descriptor '{scopeDescriptor}'.");

        var tree = await entities.GetEntityTreeAsync(tenantId, cancellationToken);
        var subtreeBranchIds = SubtreeBranchIds(tree, entityId);

        if (subtreeBranchIds.Count == 0)
            return false; // the entity no longer exists in the tree at all - cannot still be in scope.

        var viewerBranchIds = viewerPairs.Select(p => (int)p.BranchId).ToHashSet();
        return subtreeBranchIds.All(viewerBranchIds.Contains);
    }

    private static HashSet<int> SubtreeBranchIds(IReadOnlyList<EntityTreeNode> tree, int rootBranchId)
    {
        var childrenByParent = tree
            .Where(n => n.ParentId is not null)
            .GroupBy(n => n.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(n => n.BranchId).ToList());

        var result = new HashSet<int>();
        if (!tree.Any(n => n.BranchId == rootBranchId))
            return result; // root itself is not a real node in this tenant's tree.

        var stack = new Stack<int>();
        stack.Push(rootBranchId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!result.Add(current))
                continue;

            if (childrenByParent.TryGetValue(current, out var children))
                foreach (var child in children)
                    stack.Push(child);
        }

        return result;
    }
}
