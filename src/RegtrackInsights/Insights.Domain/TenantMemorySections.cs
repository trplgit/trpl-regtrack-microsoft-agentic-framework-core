using System.Text;
using System.Text.RegularExpressions;

namespace Insights.Domain;

/// <summary>
/// [ADDED 2026-09-22] Pure markdown-section helpers for the tenant memory feature (see
/// docs/superpowers/specs/2026-09-22-tenant-memory-blob-design.md). One tenant memory document
/// holds one "## {DimensionName}" section per dimension, up to the next "## " heading or EOF.
/// Sections are disjoint by construction - two dimensions never touch each other's text. No I/O,
/// no LLM involvement - the document's structure is deterministic C#, same as every other
/// structural rule in this codebase (CLAUDE.md non-negotiable #1).
/// </summary>
public static partial class TenantMemorySections
{
    [GeneratedRegex(@"^## (?<name>.+?)\r?$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    /// <summary>Returns the body text under "## {dimensionName}" (trimmed), or "" if the document
    /// is empty or that heading is absent - both are the normal, expected first-run case, not an
    /// error.</summary>
    public static string ExtractSection(string fullMarkdown, string dimensionName)
    {
        if (string.IsNullOrEmpty(fullMarkdown))
            return "";

        foreach (var (name, start, end) in EnumerateSections(fullMarkdown))
        {
            if (string.Equals(name, dimensionName, StringComparison.Ordinal))
                return fullMarkdown[start..end].Trim();
        }

        return "";
    }

    /// <summary>Replaces the body of "## {dimensionName}" with <paramref name="newSectionBody"/>,
    /// or appends a new section at the end if that heading is absent. Every other section's text
    /// and relative order is preserved exactly.</summary>
    public static string ReplaceSection(string fullMarkdown, string dimensionName, string newSectionBody)
    {
        var body = newSectionBody.TrimEnd('\n', '\r');
        var newSection = $"## {dimensionName}\n{body}\n";

        if (string.IsNullOrEmpty(fullMarkdown))
            return newSection;

        var sections = EnumerateSections(fullMarkdown).ToList();

        var result = new StringBuilder();
        var replaced = false;
        for (var i = 0; i < sections.Count; i++)
        {
            var (name, start, end) = sections[i];

            if (string.Equals(name, dimensionName, StringComparison.Ordinal))
            {
                result.Append(newSection);
                replaced = true;
            }
            else
            {
                result.Append("## ").Append(name).Append('\n');
                result.Append(fullMarkdown[start..end].Trim()).Append('\n');
            }

            if (i < sections.Count - 1)
                result.Append('\n');
        }

        if (!replaced)
        {
            if (sections.Count > 0)
                result.Append('\n');
            result.Append(newSection);
        }

        return result.ToString();
    }

    /// <summary>Yields (name, bodyStart, bodyEnd) for every "## " heading in document order. bodyEnd
    /// is the index just before the next heading, or the document's length for the last section.</summary>
    private static IEnumerable<(string Name, int Start, int End)> EnumerateSections(string fullMarkdown)
    {
        var matches = HeadingPattern().Matches(fullMarkdown);
        for (var i = 0; i < matches.Count; i++)
        {
            var name = matches[i].Groups["name"].Value.Trim();
            var bodyStart = matches[i].Index + matches[i].Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : fullMarkdown.Length;
            yield return (name, bodyStart, bodyEnd);
        }
    }
}
