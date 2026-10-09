using System.Text.Json;
using System.Text.RegularExpressions;

namespace Insights.Presentation;

/// <summary>
/// Writes a single-dimension report's REAL rows and control totals into the rendered page
/// as one JSON block: <c>&lt;script type="application/json" id="insights-data"&gt;</c> with
/// <c>{"dimension","rows","totals"}</c>, placed before the first script so chart code can read
/// it. Code writes the block; the model never re-types a row (render prompts, Section 13).
///
/// [ADDED 2026-10-09, FOUND LIVE] <see cref="Strip"/> and <see cref="RowsLost"/> exist for the
/// orchestrator's tile-QA patch loop. PatchRenderActivity hands the WHOLE page to an LLM asked to
/// "return the same document, byte-for-byte"; on three real tenant-1271 reports (Users, Act,
/// Licence) the model collapsed this block's rows array to <c>[]</c> - tens of KB of opaque row
/// JSON it was never going to reproduce faithfully - and every chart that draws from ROWS shipped
/// blank, with the run marked complete. Same failure class as the self-hosted font block
/// (PoppinsFontInjector.Strip, 2026-10-08), same cure: the LLM never sees the block, code puts
/// a guaranteed-correct copy back afterwards, and a deterministic check refuses the page if the
/// rows are gone anyway.
/// </summary>
public static partial class DimensionDataInjector
{
    public const string ElementId = "insights-data";

    public static string Inject(string html, string dimensionName, string rowsJson, string? controlTotalsJson)
    {
        var payload = "{\"dimension\":" + JsonSerializer.Serialize(dimensionName)
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

    /// <summary>
    /// Removes the data block (every copy of it) so a model-facing call never has to carry or
    /// reproduce the row JSON. Pure string transform - safe inside the orchestrator body.
    /// Pair with <see cref="Inject"/> on the way back.
    /// </summary>
    public static string Strip(string html) => ExistingBlock().Replace(html, string.Empty);

    /// <summary>
    /// True when the page SHOULD carry rows (<paramref name="expectedRowsJson"/> is a non-empty
    /// JSON array) but the data block is missing, unparsable, or its <c>rows</c> is not a
    /// non-empty array. False when nothing was expected (no rows, or no block was ever injected
    /// for this report type). A true result means a chart-driving dataset was lost somewhere
    /// between injection and persistence - refuse, never ship.
    /// </summary>
    public static bool RowsLost(string html, string? expectedRowsJson)
    {
        if (!ExpectsRows(expectedRowsJson)) return false;

        var match = ExistingBlock().Match(html);
        if (!match.Success) return true;

        var text = match.Groups["json"].Value
            .Replace("\\u003c", "<").Replace("\\u003e", ">").Replace("\\u0026", "&");
        try
        {
            using var doc = JsonDocument.Parse(text);
            return !doc.RootElement.TryGetProperty("rows", out var rows)
                || rows.ValueKind != JsonValueKind.Array
                || rows.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool ExpectsRows(string? rowsJson)
    {
        if (string.IsNullOrWhiteSpace(rowsJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(rowsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"<script\b[^>]*\bid\s*=\s*[""']insights-data[""'][^>]*>(?<json>.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ExistingBlock();
}
