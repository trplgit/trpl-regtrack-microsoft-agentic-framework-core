using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-09-27, FOUND LIVE] Puts the real dimension rows and totals into the rendered page
/// as one JSON block: <c>&lt;script type="application/json" id="insights-data"&gt;</c> with
/// <c>{"dimension":..., "rows":[...], "totals":{...}}</c>. The render prompt tells the model to build
/// every chart and table from this block instead of re-typing the rows itself.
///
/// Why: the model used to re-type every row into the page. On a 305-user tenant it silently stopped
/// at 156 users (the page even labelled them "156 matching"), and when told never to skip it ran past
/// the 5-minute call limit and failed. Rows written by code are complete and exact by construction,
/// and the model's answer gets much shorter.
///
/// Placed before the page's first script so chart code can read it at load. <c>&lt;</c>, <c>&gt;</c>
/// and <c>&amp;</c> are written as JSON \u escapes (they only ever occur inside JSON strings), so
/// tenant text can never close the script or look tag-shaped to the sanitizer. A data block the
/// model wrote itself is removed first - the real one is the only one on the page.
/// </summary>
public static partial class DimensionDataInjector
{
    public const string ElementId = "insights-data";

    public static string Inject(string html, string dimensionName, string rowsJson, string? controlTotalsJson)
    {
        var payload = "{\"dimension\":" + System.Text.Json.JsonSerializer.Serialize(dimensionName)
            + ",\"rows\":" + rowsJson
            + ",\"totals\":" + (string.IsNullOrWhiteSpace(controlTotalsJson) ? "null" : controlTotalsJson) + "}";
        var safe = payload.Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026");
        var block = $"<script type=\"application/json\" id=\"{ElementId}\">{safe}</script>";

        html = ExistingBlock().Replace(html, string.Empty);

        var firstScript = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        if (firstScript >= 0)
            return html.Insert(firstScript, block);

        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyClose >= 0 ? html.Insert(bodyClose, block) : html + block;
    }

    [GeneratedRegex(@"<script\b[^>]*\bid\s*=\s*[""']insights-data[""'][^>]*>.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ExistingBlock();
}
