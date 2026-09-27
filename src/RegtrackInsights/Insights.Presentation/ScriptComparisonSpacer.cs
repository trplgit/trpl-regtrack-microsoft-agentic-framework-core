using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// [FOUND LIVE 2026-09-27] DOMPurify deletes an entire &lt;script&gt; whose text contains "&lt;"
/// followed by a word character - a plain JS comparison like <c>page&lt;pages-1</c> or <c>v&lt;0</c>
/// reads to it as a tag. ReportEmitNormalizer does not catch this (it only looks for known tag
/// names), so a render passed every check and still reached the reader with every script-built
/// chart empty - three real renders in a row, despite an explicit prompt rule against it. This
/// inserts a space after a COMPARISON "&lt;" inside script bodies only (<c>v&lt; 0</c>): identical
/// meaning, no longer tag-shaped. Quoted markup strings and everything outside scripts are never
/// touched.
/// </summary>
public static partial class ScriptComparisonSpacer
{
    public static string Apply(string html)
    {
        if (!html.Contains("<script", StringComparison.OrdinalIgnoreCase))
            return html;

        return ScriptBlock().Replace(html, m =>
            m.Groups["open"].Value + TagShapedLessThan().Replace(m.Groups["body"].Value, "< ") + m.Groups["close"].Value);
    }

    [GeneratedRegex(@"(?<open><script\b[^>]*>)(?<body>.*?)(?<close></script>)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlock();

    // Only a comparison "<" - preceded by an identifier/number/")"/"]" - is spaced. A "<" that opens
    // a quoted string ('<span ...') is left alone: that is literal markup in a script, which the
    // normalizer and DOMPurify are meant to keep refusing, not something to disguise.
    [GeneratedRegex(@"(?<=[\w)\]])<(?=[\w/!?])")]
    private static partial Regex TagShapedLessThan();
}
