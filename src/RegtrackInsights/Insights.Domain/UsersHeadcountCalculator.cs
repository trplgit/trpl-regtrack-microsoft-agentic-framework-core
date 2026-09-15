namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-09-15] Distinct Performer/Reviewer headcount, computed deterministically in C# over
/// the already-fetched <see cref="UsersRow"/> array - not a new SQL column. sql/12_dimension_users.sql
/// already returns one row per user with per-user PerformerInstances/ReviewerInstances; this is
/// pure aggregation over data that already reconciled, the same way CompositeScoreCalculator
/// aggregates already-fetched dimension results rather than issuing new queries.
///
/// Added because the Users render prompt already asked for a "Reviewer cover
/// {performerUserCount}:{reviewerUserCount}" chip with no field ever backing it - confirmed live
/// (2026-09-15, tenant 1008) the model substitutes a different, unrelated number instead of
/// counting 300+ rows by hand, which an LLM cannot do reliably regardless of prompt wording.
/// </summary>
public static class UsersHeadcountCalculator
{
    public static (int PerformerUserCount, int ReviewerUserCount) Compute(IReadOnlyList<UsersRow> rows)
    {
        var performerUserCount = 0;
        var reviewerUserCount = 0;

        foreach (var row in rows)
        {
            if (row.PerformerInstances > 0)
                performerUserCount++;
            if (row.ReviewerInstances > 0)
                reviewerUserCount++;
        }

        return (performerUserCount, reviewerUserCount);
    }
}
