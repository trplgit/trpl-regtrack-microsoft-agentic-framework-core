using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// The signature is what collapses N recipients into one LLM call. If it is wrong, grouping
/// silently does nothing - the code still works, the cost just never moves - so these pin the
/// two properties that make it work at all.
/// </summary>
public sealed class ScopeSignatureTests
{
    /// <summary>
    /// THE ONE THAT MATTERS. SQL returns pairs in whatever order the plan produced, so two
    /// identically-scoped users must hash the same regardless of row order.
    /// </summary>
    [Fact]
    public void Signature_IsIndependentOfPairOrder()
    {
        var a = new[] { new ScopePair(10, 1), new ScopePair(20, 2), new ScopePair(30, 3) };
        var b = new[] { new ScopePair(30, 3), new ScopePair(10, 1), new ScopePair(20, 2) };

        Assert.Equal(ScopeSignature.For(a), ScopeSignature.For(b));
    }

    [Fact]
    public void Signature_IgnoresDuplicatePairs()
    {
        var withDupes = new[] { new ScopePair(10, 1), new ScopePair(10, 1), new ScopePair(20, 2) };
        var without = new[] { new ScopePair(10, 1), new ScopePair(20, 2) };

        Assert.Equal(ScopeSignature.For(without), ScopeSignature.For(withDupes));
    }

    /// <summary>Different scopes must NOT collapse - that would send one tenant's numbers to another's user.</summary>
    [Fact]
    public void Signature_DiffersWhenScopeDiffers()
    {
        var a = new[] { new ScopePair(10, 1) };
        var b = new[] { new ScopePair(10, 2) };

        Assert.NotEqual(ScopeSignature.For(a), ScopeSignature.For(b));
    }

    /// <summary>
    /// (1, 23) and (12, 3) must not collide - a separator-free join would render both as "1233".
    /// </summary>
    [Fact]
    public void Signature_DistinguishesAmbiguousIdConcatenations()
    {
        Assert.NotEqual(
            ScopeSignature.For([new ScopePair(1, 23)]),
            ScopeSignature.For([new ScopePair(12, 3)]));
    }

    /// <summary>Empty scope yields an empty signature, which the resolver uses to drop the recipient.</summary>
    [Fact]
    public void Signature_ForEmptyScope_IsEmpty()
    {
        Assert.Equal(string.Empty, ScopeSignature.For([]));
    }
}
