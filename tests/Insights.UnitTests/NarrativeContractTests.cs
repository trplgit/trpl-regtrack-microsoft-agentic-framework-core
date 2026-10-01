using Insights.Domain;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// [ADDED 2026-09-20] RowRefsUsed is optional (defaults to null) specifically so every v1 caller
/// (Entity/Users, every existing NarrateActivity/ReflectOnNarrativeActivity test) stays valid
/// unchanged. RowRefs is the safe read side of that - callers should never need a null-check.
/// </summary>
public sealed class NarrativeContractTests
{
    [Fact]
    public void RowRefs_WhenRowRefsUsedIsNull_ReturnsEmpty_NotNull()
    {
        var block = new NarrativeBlockResult("hero", "some prose", ["A-1"]);

        Assert.NotNull(block.RowRefs);
        Assert.Empty(block.RowRefs);
    }

    [Fact]
    public void RowRefs_WhenRowRefsUsedIsSupplied_ReturnsExactlyThatList()
    {
        var refs = new[] { new RowRef("Departments", "Import-Export"), new RowRef("Departments", "EXIM") };
        var block = new NarrativeBlockResult("hero", "some prose", ["A-1"], refs);

        Assert.Equal(refs, block.RowRefs);
    }
}
