using System.Text.Json;
using Insights.Domain;
using Insights.Presentation;
using Xunit.Abstractions;

namespace Insights.UnitTests;

/// <summary>
/// [ADDED 2026-10-09] Lab harness for the fabricated-number gate: replays REAL render outputs (page +
/// the exact rows / totals / assertions / data-quality notes RenderHtmlActivity checked them against,
/// exported from the prod task hub) through <see cref="ReportNumberTracer.FindUntraced"/>.
///
/// Opt-in only - does nothing unless TRACER_LAB_DIR points at a folder of fixture JSON files
/// ({name, html, rowsJson, totalsJson, assertions[], dataQualityJson, prodUntraced[]}). The fixtures
/// carry real tenant data, so they live outside the repo on purpose.
///
/// Two checks: (1) every real page traces cleanly (no false alarm); (2) the gate still catches
/// invented numbers - each page gets synthetic made-up counts and percentages appended, and the
/// catch rate is reported so a loosened candidate set can be measured, not guessed.
/// </summary>
public sealed class ReportNumberTracerRealPagesLab(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private sealed record Fixture(string Name, string Html, string? RowsJson, string? TotalsJson,
        List<Assertion>? Assertions, string? DataQualityJson, List<string>? ProdUntraced);

    [Fact]
    public void RealPages_TraceCleanly_AndInventedNumbersAreStillCaught()
    {
        var dir = Environment.GetEnvironmentVariable("TRACER_LAB_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            output.WriteLine("TRACER_LAB_DIR not set - lab skipped.");
            return;
        }

        var falseAlarms = new List<string>();
        int invented = 0, caught = 0;
        var rng = new Random(20261009);
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
        {
            var fx = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(file), Options)!;
            var notes = Notes(fx.DataQualityJson);
            var untraced = ReportNumberTracer.FindUntraced(fx.Html, fx.RowsJson, fx.TotalsJson, fx.Assertions, notes);
            output.WriteLine($"{fx.Name,-45} prod flagged [{string.Join(", ", fx.ProdUntraced ?? [])}] -> now [{string.Join(", ", untraced)}]");
            if (untraced.Count > 0)
                falseAlarms.Add($"{fx.Name}: {string.Join(", ", untraced)}");

            // Invented numbers: 40 counts (13..20,000) and 40 one-decimal percentages, each alone in a
            // copy of the real page. A value that happens to equal a real derivation counts as "not
            // caught" - that is exactly the density being measured.
            for (var k = 0; k < 80; k++)
            {
                var token = k < 40 ? rng.Next(13, 20_001).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                                   : (rng.Next(1, 1000) / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
                var page = fx.Html.Replace("</body>", $"<p>Invented claim: {token} items.</p></body>");
                invented++;
                if (ReportNumberTracer.FindUntraced(page, fx.RowsJson, fx.TotalsJson, fx.Assertions, notes).Contains(token))
                    caught++;
            }
        }

        output.WriteLine($"Invented numbers caught: {caught} of {invented} ({100.0 * caught / Math.Max(1, invented):0.0}%)");
        Assert.Empty(falseAlarms);
    }

    private static List<string> Notes(string? dataQualityJson)
    {
        if (string.IsNullOrWhiteSpace(dataQualityJson)) return [];
        using var doc = JsonDocument.Parse(dataQualityJson);
        return doc.RootElement.ValueKind != JsonValueKind.Array ? [] :
            doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Detail", out _))
                .Select(e => e.GetProperty("Detail").GetString() ?? "").ToList();
    }
}
