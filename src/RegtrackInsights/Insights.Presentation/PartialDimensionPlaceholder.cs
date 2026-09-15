using System.Net;
using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// Design doc Sec.11.4 ("Partial generation"): "Render the successful blocks; in the failed
/// block's slot, render an explicit placeholder... a missing section always announces itself."
///
/// DETERMINISTIC, never agent-authored - the composition/narrative agents are simply never given
/// a failed dimension's data (FetchDimensionsActivity omits it from DimensionResults entirely), so
/// they cannot narrate about it either way. This class is the ONLY thing that ever states a
/// dimension failed, and it does so with one fixed template per dimension - matches CLAUDE.md
/// non-negotiable 1: the agent decides what matters, never who may see it or what got hidden.
///
/// Runs in InsightsReportOrchestrator BETWEEN RenderHtmlActivity and the first NormalizeActivity
/// call, not after the safety pipeline - the placeholder markup is plain semantic HTML with only
/// inline styling (no script, no external reference), so it is validated by
/// ReportEmitNormalizer/DOMPurify exactly like the rest of the document rather than being smuggled
/// in downstream of where anything would ever check it. Pure string transform, no I/O, no clock -
/// safe to call directly from the orchestrator body (CLAUDE.md 6: orchestrator determinism).
/// </summary>
public static partial class PartialDimensionPlaceholder
{
    /// <summary>
    /// The nine real dimension names (IDimensionRepository's GetXAsync methods) mapped to the
    /// phrase that fills design doc Sec.11.4's template: "The {name} analysis could not be
    /// generated for this report." Closed set on purpose - an unrecognised key falls back to the
    /// raw dimension name rather than throwing, since a labelled-but-slightly-generic placeholder
    /// is still infinitely better than no placeholder at all if a tenth dimension is ever added
    /// and this map is not updated in the same change.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AnalysisNames = new Dictionary<string, string>
    {
        ["Location"] = "location-coverage",
        ["Entity"] = "entity-hierarchy",
        ["Risk"] = "risk-tier",
        ["Nature"] = "nature-of-compliance",
        ["Departments"] = "departmental-ownership",
        ["Act"] = "applicable-Acts",
        ["Users"] = "user-workload",
        ["Internal"] = "internal-vs-statutory",
        ["Event"] = "event-triggered-compliance",
    };

    /// <summary>
    /// Inserts one placeholder block per failed dimension just before &lt;/body&gt; (or
    /// &lt;/html&gt; if no &lt;/body&gt; is found - the prompt requires the document to end with
    /// &lt;/html&gt;, but does not itself require a &lt;body&gt; tag to exist). A no-op when
    /// nothing failed - the overwhelmingly common case must cost nothing and change nothing.
    /// </summary>
    public static string InsertPlaceholders(string html, IReadOnlyList<string> failedDimensions)
    {
        if (failedDimensions.Count == 0)
            return html;

        var block = string.Concat(failedDimensions.Select(BuildBlock));

        var bodyClose = CloseBodyToken().Match(html);
        if (bodyClose.Success)
            return html[..bodyClose.Index] + block + html[bodyClose.Index..];

        var htmlClose = CloseHtmlToken().Match(html);
        return htmlClose.Success
            ? html[..htmlClose.Index] + block + html[htmlClose.Index..]
            : html + block;
    }

    private static string BuildBlock(string dimension)
    {
        var name = AnalysisNames.TryGetValue(dimension, out var friendly) ? friendly : dimension;
        var encodedName = WebUtility.HtmlEncode(name);
        return "<section style=\"margin:1.5em 0;padding:1em;border:1px solid #c0392b;background:#fdf1f0;color:#7a231b;\">"
            + "<strong>Section unavailable:</strong> The " + encodedName
            + " analysis could not be generated for this report."
            + "</section>";
    }

    [GeneratedRegex(@"</body\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CloseBodyToken();

    [GeneratedRegex(@"</html\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CloseHtmlToken();
}
