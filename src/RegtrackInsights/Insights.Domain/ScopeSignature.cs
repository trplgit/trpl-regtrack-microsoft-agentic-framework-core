using System.Security.Cryptography;
using System.Text;

namespace Insights.Domain;

/// <summary>
/// A stable fingerprint of one caller's authorised (branch, category) pairs.
///
/// -- WHY THIS EXISTS ------------------------------------------------------------------------
/// The free digest computes its aggregates inside the recipient's scope, so two recipients with
/// the SAME pairs receive byte-identical numbers - and therefore need only ONE LLM call between
/// them, not two.
///
/// That matters at the top end. Design doc 10.5 budgets the whole free tier at roughly 30M
/// tokens a year across ~600 tenants, which assumes about one digest per tenant per week. One
/// call per RECIPIENT breaks that arithmetic: a single tenant with 1,000 management users would
/// spend more than the entire annual budget in one week. Most of those users are tenant-wide, so
/// grouping collapses them to a single call.
///
/// -- [TRAP] ORDER INDEPENDENCE IS THE WHOLE POINT --------------------------------------------
/// The pairs come back from SQL in whatever order the plan produced. If the signature depended on
/// row order, two identically-scoped users would hash differently and grouping would silently do
/// nothing - the code would look correct and the cost would not move. Hence the explicit sort.
/// </summary>
public static class ScopeSignature
{
    /// <summary>Order-independent signature over a set of scope pairs. Empty scope yields an empty string.</summary>
    public static string For(IEnumerable<ScopePair> pairs) => For(pairs, []);

    /// <summary>
    /// [ADDED 2026-09-29] Signature over BOTH scopes the free digest reads: compliance pairs and
    /// licence pairs (LIC_EntitiesAssignment, sql/35). Two recipients share one email - one set of
    /// numbers, one representative's data - so they must match on every scope that data is cut
    /// by. Keyed on compliance pairs alone, a user with the same compliance scope but FEWER
    /// licence assignments would have received licences they are not assigned.
    ///
    /// <para>An empty compliance scope is still DENY (empty string) whatever the licences say. An
    /// empty licence scope adds nothing, so a user with no licence assignments keeps exactly the
    /// signature <see cref="For(IEnumerable{ScopePair})"/> always gave them. The licence part is
    /// behind a "|L|" marker the compliance part can never contain, so the two cannot collide.</para>
    /// </summary>
    public static string For(IEnumerable<ScopePair> pairs, IEnumerable<LicenceScopePair> licencePairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(licencePairs);

        var canonical = string.Join(
            ';',
            pairs.Select(p => $"{p.BranchId}:{p.CategoryId}")
                 .Distinct(StringComparer.Ordinal)
                 .OrderBy(s => s, StringComparer.Ordinal));

        if (canonical.Length == 0)
            return string.Empty;

        var licences = string.Join(
            ';',
            licencePairs.Select(p => $"{p.BranchId}:{p.LicenseTypeId}")
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(s => s, StringComparer.Ordinal));

        if (licences.Length > 0)
            canonical += "|L|" + licences;

        // Hashed rather than stored raw: a tenant-wide user can hold thousands of pairs, and this
        // value is only ever compared for equality, never read back.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
