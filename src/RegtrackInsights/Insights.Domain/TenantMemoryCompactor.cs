using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-09-28] Size control for one dimension's tenant-memory section - deterministic, no
/// LLM call. The MODEL decides what is important; this class only ever removes what the model
/// itself marked as less important, in a fixed order, so month-over-month comparisons keep what
/// they need:
///
/// <list type="number">
/// <item><c>### Keep</c> - facts the model says must survive (baselines per period with dates,
///   recurring problems, first-seen dates). NEVER shortened or removed.</item>
/// <item><c>### YYYY-MM-DD (period)</c> - one entry per run, FIRST point = that run's most important
///   finding. Over the limit, the oldest entries are shortened to heading + first point (the two
///   newest always stay whole); only if that is still not enough are the oldest entries removed,
///   with a line saying how many.</item>
/// </list>
///
/// Anything else (text before the first heading, undated headings) is treated as older than every
/// dated run. If the Keep block alone takes more than half the limit, compaction refuses (null) and
/// the model is asked to decide what to cut - code never guesses which "must keep" fact matters less.
/// </summary>
public static partial class TenantMemoryCompactor
{
    /// <summary>Text is null exactly when Refusal is set - nothing may be stored then.</summary>
    public sealed record Result(string? Text, bool Changed, int Shortened, int Removed, string? Refusal = null);

    public const string KeepTooLarge = "keep_too_large";
    public const string EntryTooLarge = "entry_too_large";

    private const int AlwaysWholeRuns = 2;

    /// <summary>
    /// A "# " or "## " line in the model's text would start a NEW dimension section in the tenant's
    /// file (TenantMemorySections splits on "## ") - letting one dimension's write overwrite another
    /// dimension's memory. Every such line becomes a "### " sub-heading.
    /// </summary>
    public static string SanitizeHeadings(string markdown) =>
        TopLevelHeading().Replace(markdown.Replace("\r\n", "\n"), "${indent}### ");

    public static Result Compact(string markdown, int maxChars)
    {
        var text = markdown.Replace("\r\n", "\n").TrimEnd();
        var blocks = Parse(text);
        var keep = string.Join("\n", blocks.Where(b => b.IsKeep).Select(b => b.Text.TrimEnd()));

        // Checked on EVERY write, not only when compaction is needed: an oversized Keep that happens
        // to fit today would otherwise be refused on some later run instead of now, while the model
        // that wrote it can still decide what matters most.
        if (keep.Length > maxChars / 2)
            return new Result(null, false, 0, 0, KeepTooLarge);

        if (text.Length <= maxChars)
            return new Result(text, Changed: false, 0, 0);

        var runs = blocks.Where(b => !b.IsKeep)
            .OrderByDescending(b => b.Date ?? DateOnly.MinValue)
            .Select(b => new RunState(b))
            .ToList();

        string Render(int removed)
        {
            var sb = new StringBuilder();
            if (keep.Length > 0) sb.Append(keep).Append("\n\n");
            foreach (var r in runs.Where(r => !r.Dropped))
                sb.Append(r.Shortened ? r.Block.Headline : r.Block.Text.TrimEnd()).Append("\n\n");
            if (removed > 0)
                sb.Append("### Older runs\n- ").Append(removed.ToString(CultureInfo.InvariantCulture))
                  .Append(" older run(s) removed to stay within the size limit - only what is in Keep above was carried forward.\n");
            return sb.ToString().TrimEnd();
        }

        Result Done(int removed) =>
            new(Render(removed), Changed: true, runs.Count(r => r.Shortened && !r.Dropped), removed);

        if (Render(0).Length <= maxChars) return Done(0);

        // 1. Shorten the oldest runs to their most important point, keeping the newest whole.
        for (var i = runs.Count - 1; i >= AlwaysWholeRuns; i--)
        {
            runs[i].Shortened = true;
            if (Render(0).Length <= maxChars) return Done(0);
        }

        // 2. Remove the oldest (already shortened) runs, one at a time.
        var removedCount = 0;
        for (var i = runs.Count - 1; i >= AlwaysWholeRuns; i--)
        {
            runs[i].Dropped = true;
            removedCount++;
            if (Render(removedCount).Length <= maxChars) return Done(removedCount);
        }

        // 3. Last resort: shorten the newest runs too (never remove them).
        for (var i = Math.Min(AlwaysWholeRuns, runs.Count) - 1; i >= 0; i--)
        {
            runs[i].Shortened = true;
            if (Render(removedCount).Length <= maxChars) return Done(removedCount);
        }

        // Even every run cut to its heading + first point does not fit: some single line is huge.
        return new Result(null, false, 0, 0, EntryTooLarge);
    }

    /// <summary>
    /// [ADDED 2026-09-28] What a summariser gets when a section is over the limit: ONLY the runs
    /// older than the two newest (plus any earlier summary block), and how many characters its one
    /// summary block may use so Keep + the two newest runs + the summary fit.
    /// </summary>
    public sealed record SummaryPlan(string Keep, IReadOnlyList<string> NewestRuns, string OlderEntries, int OlderCount, int SummaryBudget);

    public const string SummaryHeadingPrefix = "### Summary of older runs";

    /// <summary>Null when nothing needs summarising (under the limit, Keep too large, or no older runs).</summary>
    public static SummaryPlan? PlanSummary(string markdown, int maxChars)
    {
        var text = markdown.Replace("\r\n", "\n").TrimEnd();
        if (text.Length <= maxChars)
            return null;

        var blocks = Parse(text);
        var keep = string.Join("\n", blocks.Where(b => b.IsKeep).Select(b => b.Text.TrimEnd()));
        if (keep.Length > maxChars / 2)
            return null;

        var runs = blocks.Where(b => !b.IsKeep).OrderByDescending(b => b.Date ?? DateOnly.MinValue).ToList();
        var newest = runs.Take(AlwaysWholeRuns).Select(b => b.Text.TrimEnd()).ToList();
        var older = runs.Skip(AlwaysWholeRuns).ToList();
        if (older.Count == 0)
            return null;

        var fixedPart = (keep.Length > 0 ? keep.Length + 2 : 0) + newest.Sum(n => n.Length + 2);
        var budget = maxChars - fixedPart;
        return new SummaryPlan(keep, newest,
            string.Join("\n\n", older.Select(b => b.Text.TrimEnd())),
            older.Count(b => b.Date is not null), budget);
    }

    /// <summary>
    /// One block, starting with the summary heading, no "#"/"##" line (that would start another
    /// dimension's section in the tenant file), within budget. Anything else is not stored.
    /// </summary>
    public static bool IsValidSummary(string? summary, int budget)
    {
        if (string.IsNullOrWhiteSpace(summary) || budget <= 0)
            return false;
        var s = summary.Replace("\r\n", "\n").Trim();
        return s.StartsWith(SummaryHeadingPrefix, StringComparison.Ordinal)
               && s.Length <= budget
               && !TopLevelHeading().IsMatch(s)
               && s.Split('\n').Count(l => l.StartsWith("### ", StringComparison.Ordinal)) == 1;
    }

    /// <summary>Keep, then the two newest runs whole, then the summary of everything older.</summary>
    public static string Assemble(SummaryPlan plan, string summary)
    {
        var parts = new List<string>();
        if (plan.Keep.Length > 0) parts.Add(plan.Keep);
        parts.AddRange(plan.NewestRuns);
        parts.Add(summary.Replace("\r\n", "\n").Trim());
        return string.Join("\n\n", parts);
    }

    private sealed class RunState(Block block)
    {
        public Block Block { get; } = block;
        public bool Shortened { get; set; }
        public bool Dropped { get; set; }
    }

    private sealed record Block(string Text, bool IsKeep, DateOnly? Date)
    {
        /// <summary>The heading plus the first point - the model's own most important line.</summary>
        public string Headline
        {
            get
            {
                var lines = Text.Split('\n');
                var heading = lines[0].StartsWith("### ", StringComparison.Ordinal) ? lines[0] : "### Notes";
                var first = lines.Skip(lines[0].StartsWith("### ", StringComparison.Ordinal) ? 1 : 0)
                    .FirstOrDefault(l => l.Trim().Length > 0) ?? "";
                return first.Length == 0 ? heading : $"{heading}\n{first.TrimEnd()}";
            }
        }
    }

    private static List<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        var current = new StringBuilder();
        void Flush()
        {
            var t = current.ToString();
            if (t.Trim().Length == 0) { current.Clear(); return; }
            var first = t.Split('\n')[0];
            var isHeading = first.StartsWith("### ", StringComparison.Ordinal);
            var title = isHeading ? first[4..].Trim() : "";
            var isKeep = isHeading && title.StartsWith("Keep", StringComparison.OrdinalIgnoreCase);
            DateOnly? date = isHeading && DateOnly.TryParseExact(title.Length >= 10 ? title[..10] : "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;
            blocks.Add(new Block(t, isKeep, date));
            current.Clear();
        }
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("### ", StringComparison.Ordinal)) Flush();
            current.Append(line).Append('\n');
        }
        Flush();
        return blocks;
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)#{1,2}(?!#)[ \t]+", RegexOptions.Multiline)]
    private static partial Regex TopLevelHeading();
}
