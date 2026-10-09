using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Insights.Domain;

namespace Insights.Presentation;

/// <summary>
/// [ADDED 2026-09-27] The fabricated-number gate (CLAUDE.md non-negotiable #2/#5): every number the
/// render model TYPED into the finished report must trace back to the real data - a value in the
/// rows, the control totals, an assertion or the already-verified narrative, or a simple derivation
/// of those (a share, a gap, a sum, a count of rows, a median). Numbers the page's own scripts
/// compute at view time from the injected #insights-data block never appear in the static HTML, so
/// they are not checked here - they come from the real rows by construction.
///
/// Two pools, kept apart on purpose: counts (whole things - people, obligations, laws) and
/// percentages (shares and point gaps). A "%" on the report only matches the percentage pool, a
/// plain number only the count pool (or the percentage pool when it is a "points" gap). Shares are
/// only built between a PART and its WHOLE - a field that is never larger than another on any row
/// (Overdue within Instances) - never between every pair of fields: with every pair, the share
/// space is so dense that almost any invented percentage lands near one (measured: 91% of random
/// one-decimal percentages passed). "Points above average" gaps sit in a third pool, used only when
/// the report says "points"/"pp" next to the number. Pure, deterministic, no I/O.
/// </summary>
public static partial class ReportNumberTracer
{
    /// <summary>Counting words and list positions ("top 5", "3 things", "tab 2") - never a data claim on their own.</summary>
    private const int SmallIntegerCeiling = 12;

    /// <summary>A string field with more distinct values than this is a name, not a grouping (person, law).</summary>
    private const int MaxGroupValues = 40;

    /// <summary>Running totals of the biggest rows ("the top 5 people carry ...").</summary>
    private const int MaxTopK = 20;

    /// <summary>
    /// Day windows and age-bucket edges the SQL itself defines (period lengths 30/60/90/180, the
    /// Users timing outlier window of 365 days, 12-month login lookback) - method, not data.
    /// </summary>
    private static readonly double[] MethodConstants = [30, 45, 60, 90, 180, 365];

    public sealed record Candidates(double[] Counts, double[] Percentages, double[] Gaps);

    public static IReadOnlyList<string> FindUntraced(
        string html,
        string? rowsJson,
        string? totalsJson,
        IEnumerable<Assertion>? assertions,
        IEnumerable<string?>? trustedTexts)
    {
        // [FIX 2026-10-09, found live on prod tenant 1271 - Location + Departments refused on every
        // attempt of 3 runs] Two kinds of model-typed text are method, not data, and are dropped
        // before numbers are read: the mini illustration of a "How to read this chart" panel
        // (.hr-viz - the render prompts define it as an "optional mini inline-SVG illustration";
        // a sample scale "1 / 10 / 100 / 1,000" there teaches the reader, it claims nothing), and the
        // "x 100" multiplier of a percentage formula ("669 / 927 x 100"). The panel's written
        // explanation and every number in the formula itself are still checked.
        var text = TimesHundred().Replace(ReportClaimExtractor.VisibleText(HowToReadIllustration().Replace(html, " ")), " ");
        // [FIX 2026-10-09, found live after the fix above] A model-typed chart axis - "0 256 512 768
        // 1,024": a run of 4+ numbers starting at 0 in equal steps. The steps between 0 and the end
        // are scale marks (fractions of the axis end), not claims; the end itself is still checked.
        text = EvenScale().Replace(text, m => DropScaleSteps(m.Value));
        var numbers = ReportClaimExtractor.ExtractNumbers(text, ReportClaimExtractor.NumbersToIgnore(rowsJson));
        if (numbers.Count == 0)
            return [];

        var candidates = BuildCandidates(rowsJson, totalsJson, assertions, trustedTexts);
        return numbers.Where(n => !IsTraced(n, candidates, IsPointsGap(text, n))).ToList();
    }

    /// <summary>
    /// "0 256 512 768 1,024 28" -> "0 1,024 28": the stretch that climbs from 0 in equal steps (4+
    /// numbers) loses its inner steps; its end and anything after it stay. Unchanged otherwise.
    /// </summary>
    private static string DropScaleSteps(string run)
    {
        var tokens = run.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var values = tokens.Select(t => double.Parse(t.Replace(",", ""), CultureInfo.InvariantCulture)).ToArray();
        var kept = new List<string>();
        var i = 0;
        while (i < tokens.Length)
        {
            var end = i;
            if (values[i] == 0 && i + 1 < values.Length && values[i + 1] > 0)
            {
                var step = values[i + 1];
                end = i + 1;
                while (end + 1 < values.Length && Math.Abs(values[end + 1] - values[end] - step) < 1e-9) end++;
            }
            if (end - i + 1 >= 4)
            {
                kept.Add(tokens[i]);
                kept.Add(tokens[end]);
                i = end + 1;
            }
            else
            {
                kept.Add(tokens[i]);
                i++;
            }
        }
        return string.Join(" ", kept);
    }

    /// <summary>"15.2 points", "3 pp" - a percentage gap written without a % sign.</summary>
    private static bool IsPointsGap(string text, string token) =>
        Regex.IsMatch(text, @"(?<![\w.,])" + Regex.Escape(token) + @"\s*(?:percentage\s+)?(?:points?|pp|pts?)\b", RegexOptions.IgnoreCase);

    internal static bool IsTraced(string token, Candidates candidates, bool pointsGap = false)
    {
        var isPercent = token.EndsWith('%');
        var bare = token.TrimEnd('%').Replace(",", "");
        if (!double.TryParse(bare, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return true; // not a number we can reason about - never refuse on a parse quirk

        // "84.80" is 84.8 padded to two places - judge it at the precision it really carries.
        var significant = bare.Contains('.') ? bare.TrimEnd('0').TrimEnd('.') : bare;
        var decimals = significant.Contains('.') ? significant.Length - significant.IndexOf('.') - 1 : 0;
        if (decimals == 0 && value <= SmallIntegerCeiling && !isPercent)
            return true;
        if (isPercent && decimals == 0 && value is 0 or 100)
            return true;

        var tolerance = 0.5 * Math.Pow(10, -decimals) + 1e-9;
        if (pointsGap)
            return AnyWithin(candidates.Gaps, value - tolerance, value + tolerance)
                || AnyWithin(candidates.Percentages, value - tolerance, value + tolerance);
        if (isPercent)
            return AnyWithin(candidates.Percentages, value - tolerance, value + tolerance);
        return AnyWithin(candidates.Counts, value - tolerance, value + tolerance);
    }

    private static bool AnyWithin(double[] sorted, double low, double high)
    {
        var i = Array.BinarySearch(sorted, low);
        if (i < 0) i = ~i;
        return i < sorted.Length && sorted[i] <= high;
    }

    internal static Candidates BuildCandidates(string? rowsJson, string? totalsJson, IEnumerable<Assertion>? assertions, IEnumerable<string?>? trustedTexts)
    {
        var counts = new HashSet<double>();
        var pcts = new HashSet<double>();
        var gaps = new HashSet<double>();
        void G(double v) { if (double.IsFinite(v)) gaps.Add(Math.Round(Math.Abs(v), 6)); }
        foreach (var m in MethodConstants) C(m);
        void C(double v) { if (double.IsFinite(v)) counts.Add(Math.Round(Math.Abs(v), 6)); }
        void P(double v) { if (double.IsFinite(v)) pcts.Add(Math.Round(Math.Abs(v), 6)); }
        void Share(double part, double whole) { if (whole != 0) P(part / whole * 100); }
        void Both(double v) { C(v); P(v); }

        var totals = NumericFields(Parse(totalsJson));
        foreach (var (name, v) in totals)
            if (IsPercentName(name)) P(v); else C(v);
        var totalCounts = totals.Where(t => !IsPercentName(t.Key)).Select(t => t.Value).ToList();
        foreach (var part in totalCounts)
            foreach (var whole in totalCounts)
                if (part < whole) { Share(part, whole); C(whole - part); }
        // [FIX 2026-10-09, found live] A plain sum of two totals - "all due dates" written as
        // AssignedInstances + UnassignedInstances (7,202 + 33 = 7,235 on Departments 1271). The
        // class doc already promises sums; only differences and shares of totals were built.
        for (var i = 0; i < totalCounts.Count; i++)
            for (var j = i + 1; j < totalCounts.Count; j++)
                C(totalCounts[i] + totalCounts[j]);
        var totalPcts = totals.Where(t => IsPercentName(t.Key)).Select(t => t.Value).ToList();

        var rows = new List<Dictionary<string, double>>();
        var rowStrings = new List<Dictionary<string, string>>();
        if (Parse(rowsJson) is { ValueKind: JsonValueKind.Array } rowArray)
        {
            foreach (var row in rowArray.EnumerateArray())
            {
                rows.Add(NumericFields(row));
                rowStrings.Add(StringFields(row));
            }
        }
        C(rows.Count);

        var fields = rows.SelectMany(r => r.Keys).Distinct().ToList();
        var countFields = fields.Where(f => !IsPercentName(f)).ToList();
        var pctFields = fields.Where(IsPercentName).ToList();

        // Part-of-whole pairs: never larger on any row, and non-zero somewhere.
        var partWhole = new List<(string Part, string Whole)>();
        foreach (var a in countFields)
            foreach (var b in countFields)
            {
                if (a == b) continue;
                var both = rows.Where(r => r.ContainsKey(a) && r.ContainsKey(b)).ToList();
                if (both.Count > 0 && both.All(r => r[a] <= r[b]) && both.Any(r => r[b] > 0) && both.Any(r => r[a] != r[b]))
                    partWhole.Add((a, b));
            }

        var columnSums = new Dictionary<string, double>();
        foreach (var f in fields)
        {
            var col = rows.Where(r => r.ContainsKey(f)).Select(r => r[f]).ToList();
            if (col.Count == 0) continue;
            var isPct = IsPercentName(f);
            void K(double v) { if (isPct) P(v); else C(v); }

            foreach (var v in col) K(v);
            K(col.Average());
            K(Median(col));
            K(col.Max());
            K(col.Min());
            var positive = col.Count(v => v > 0);
            var zero = col.Count(v => v == 0);
            var aboveAverage = col.Count(v => v > col.Average());
            foreach (var n in new[] { positive, zero, aboveAverage, col.Count })
            {
                C(n);
                Share(n, rows.Count);
                Share(n, col.Count);
            }
            if (isPct)
            {
                // Each row's rate against the whole-tenant rate / the average rate ("points above").
                foreach (var v in col)
                {
                    foreach (var t in totalPcts) G(v - t);
                    G(v - col.Average());
                    G(v - Median(col));
                }
                continue;
            }

            var sum = col.Sum();
            columnSums[f] = sum;
            C(sum);
            foreach (var v in col)
            {
                Share(v, sum);
                foreach (var t in totalCounts)
                    if (col.Max() <= t) { Share(v, t); C(t - v); }
            }
            var desc = col.OrderByDescending(v => v).ToList();
            double running = 0;
            for (var k = 0; k < Math.Min(desc.Count, MaxTopK); k++)
            {
                running += desc[k];
                C(running);
                Share(running, sum);
                foreach (var t in totalCounts)
                    if (sum <= t * 2) Share(running, t);
            }
            foreach (var t in totalCounts)
            {
                if (col.Max() <= t) Share(sum, t);
                // The residual - the part of a total no row covers (overdue work with no
                // department) - and its rate against every other total ("70.6% of unassigned").
                if (sum <= t)
                {
                    C(t - sum);
                    foreach (var u in totalCounts)
                        if (t - sum <= u) Share(t - sum, u);
                }
            }
        }

        foreach (var (part, whole) in partWhole)
        {
            var ratios = new List<double>();
            foreach (var r in rows)
            {
                if (!r.TryGetValue(part, out var a) || !r.TryGetValue(whole, out var b)) continue;
                C(b - a);
                if (b != 0) ratios.Add(a / b * 100);
            }
            foreach (var x in ratios) P(x);
            var overall = columnSums.GetValueOrDefault(whole) is var sw && sw != 0 ? columnSums.GetValueOrDefault(part) / sw * 100 : (double?)null;
            if (overall is { } o) P(o);
            if (columnSums.TryGetValue(part, out var sp) && columnSums.TryGetValue(whole, out var sw2)) C(sw2 - sp);
            if (ratios.Count == 0) continue;
            P(Median(ratios));
            P(ratios.Average());
            var atFull = ratios.Count(x => Math.Abs(x - 100) < 1e-9);
            var atZero = ratios.Count(x => x == 0);
            var overHalf = ratios.Count(x => x >= 50);
            foreach (var n in new[] { atFull, atZero, overHalf, ratios.Count })
            {
                C(n);
                Share(n, ratios.Count);
                Share(n, rows.Count);
            }
            foreach (var x in ratios)
            {
                if (overall is { } ov) G(x - ov);
                foreach (var t in totalPcts) G(x - t);
            }
            if (overall is { } ov2) C(ratios.Count(x => x > ov2));
        }

        // Groups: a string field with a few distinct values (role, risk band, engagement) - rows per
        // group and their share, and each count field summed per group with its share.
        foreach (var sf in rowStrings.SelectMany(r => r.Keys).Distinct())
        {
            var groups = rows.Select((r, i) => (Row: r, Key: rowStrings[i].GetValueOrDefault(sf)))
                .Where(x => x.Key is not null).GroupBy(x => x.Key!).ToList();
            if (groups.Count <= MaxGroupValues)
            {
                foreach (var g in groups)
                {
                    var n = g.Count();
                    C(n);
                    Share(n, rows.Count);
                    foreach (var f in countFields)
                    {
                        var s = g.Sum(x => x.Row.GetValueOrDefault(f));
                        C(s);
                        if (columnSums.TryGetValue(f, out var cs)) Share(s, cs);
                    }
                }
            }
            // Comma-separated flag lists ("adoption_lag, sole_reviewer"): rows carrying each flag.
            foreach (var flag in rowStrings.Select(r => r.GetValueOrDefault(sf)).Where(v => v is not null && v.Contains(','))
                         .SelectMany(v => v!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct())
            {
                var n = rowStrings.Count(r => (r.GetValueOrDefault(sf) ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(flag));
                C(n);
                Share(n, rows.Count);
            }
        }

        foreach (var a in assertions ?? [])
        {
            var value = (double)a.Value;
            Both(value);
            if (a.Rank is { } rank) C(rank);
            if (a.OfN is { } ofN) { C(ofN); Share(value, ofN); C(ofN - value); if (a.Rank is { } r) Share(r, ofN); }
            if (a.ComparatorValue is { } cv) { Both((double)cv); G(value - (double)cv); C(value - (double)cv); Share(value, (double)cv); }
            if (a.VsComparatorPP is { } pp) G((double)pp);
            foreach (var t in totalCounts) if (value <= t) Share(value, t);
            foreach (Match m in NumberInText().Matches(a.Caveat ?? ""))
                if (TryParse(m.Value, out var v)) Both(v);
        }

        // Already-verified prose (the narrative passed the publish gate before render ran).
        foreach (var t in trustedTexts ?? [])
            foreach (Match m in NumberInText().Matches(t ?? ""))
                if (TryParse(m.Value, out var v)) Both(v);

        return new Candidates(Sorted(counts), Sorted(pcts), Sorted(gaps));
    }

    private static bool IsPercentName(string name) =>
        name.EndsWith("Pct", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Percent", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Rate", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Share", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("PP", StringComparison.Ordinal);

    private static double[] Sorted(HashSet<double> set)
    {
        var a = set.ToArray();
        Array.Sort(a);
        return a;
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, double> NumericFields(JsonElement? obj)
    {
        var result = new Dictionary<string, double>();
        if (obj is not { ValueKind: JsonValueKind.Object } o) return result;
        foreach (var p in o.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d)
                && !p.Name.EndsWith("ID", StringComparison.Ordinal) && !p.Name.EndsWith("Id", StringComparison.Ordinal))
                result[p.Name] = d;
            else if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                result[p.Name] = p.Value.GetBoolean() ? 1 : 0;
        }
        return result;
    }

    private static Dictionary<string, string> StringFields(JsonElement obj)
    {
        var result = new Dictionary<string, string>();
        if (obj.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in obj.EnumerateObject())
            if (p.Value.ValueKind is JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s)
                result[p.Name] = s;
            else if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                result[p.Name] = p.Value.GetBoolean() ? "true" : "false";
        return result;
    }

    private static double Median(List<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static bool TryParse(string token, out double value) =>
        double.TryParse(token.TrimEnd('%').Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(@"\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?")]
    private static partial Regex NumberInText();

    // The .hr-viz illustration block of a "How to read this chart" panel (one inline SVG, no nested div).
    [GeneratedRegex(@"<div\b[^>]*\bclass\s*=\s*[""'][^""']*\bhr-viz\b[^""']*[""'][^>]*>.*?</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HowToReadIllustration();

    // 4+ whole numbers (no %, no decimals) separated only by whitespace - a candidate axis/scale run.
    [GeneratedRegex(@"(?<![\w.,])(?:\d{1,3}(?:,\d{3})+|\d+)(?:\s+(?:\d{1,3}(?:,\d{3})+|\d+)){3,}(?![\w.,%])")]
    private static partial Regex EvenScale();

    // "x 100" / "* 100" - the percentage multiplier of a formula, in visible text (&times; already decoded).
    [GeneratedRegex(@"[×✕✖*]\s*100(?![\d.,%])")]
    private static partial Regex TimesHundred();
}
