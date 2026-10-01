using Insights.Presentation;

namespace Insights.UnitTests;

public sealed class ScriptComparisonSpacerTests
{
    [Theory]
    [InlineData("<script>if(page<pages-1){x()}</script>", "<script>if(page< pages-1){x()}</script>")]
    [InlineData("<script>return v<0;</script>", "<script>return v< 0;</script>")]
    [InlineData("<script>a<=b</script>", "<script>a<=b</script>")]
    [InlineData("<script>i < n</script>", "<script>i < n</script>")]
    [InlineData("<script type=\"text/javascript\">k<len</script>", "<script type=\"text/javascript\">k< len</script>")]
    [InlineData("<script>if(f(x)<y[0])z()</script>", "<script>if(f(x)< y[0])z()</script>")]
    [InlineData("<script>if(a[i]<b)z()</script>", "<script>if(a[i]< b)z()</script>")]
    [InlineData("<script>el.innerHTML='<span>';</script>", "<script>el.innerHTML='<span>';</script>")]
    [InlineData("<script>s=\"<div class='x'>\";</script>", "<script>s=\"<div class='x'>\";</script>")]
    public void SpacesLessThanFollowedByAWordCharacterInsideScripts(string input, string expected) =>
        Assert.Equal(expected, ScriptComparisonSpacer.Apply(input));

    [Fact]
    public void LeavesMarkupOutsideScriptsUntouched()
    {
        const string html = "<html><body><p>a<b</p><svg><circle r=\"3\"/></svg><script>q<r</script><div>x</div></body></html>";

        var result = ScriptComparisonSpacer.Apply(html);

        Assert.Equal("<html><body><p>a<b</p><svg><circle r=\"3\"/></svg><script>q< r</script><div>x</div></body></html>", result);
    }

    [Fact]
    public void HandlesMultipleScripts()
    {
        const string html = "<script>a<1</script><p>t</p><script>b<c</script>";

        Assert.Equal("<script>a< 1</script><p>t</p><script>b< c</script>", ScriptComparisonSpacer.Apply(html));
    }

    [Fact]
    public void ReturnsDocumentsWithoutScriptsUnchanged()
    {
        const string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><h1>R</h1></body></html>";

        Assert.Same(html, ScriptComparisonSpacer.Apply(html));
    }
}
