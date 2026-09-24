using System.Text.Json;
using System.Text.RegularExpressions;
using Insights.Agents;

namespace Insights.Worker;

/// <summary>
/// An <see cref="IClaudeClient"/> that answers with a draft the model ALREADY wrote, read from a
/// preview's <c>.debug.txt</c>, so the deterministic layers after the model (normalise, repair,
/// validate, bind, render) can be re-run without a billed call.
///
/// <para>[OWNER, 2026-09-23] Presentation is decided in code, not by the model. When the emphasis
/// rules change, the right test is the same draft through the new rules - and the wrong test is
/// another model call, which costs tokens and, being a different draft, hides whether the change
/// did what it should. <c>FreeDigest:Preview:DraftsDir</c> points at a folder of earlier previews;
/// the slot is read from the user message the writer sends, and the draft comes from that slot's
/// debug file. Development only: registered only when the preview is enabled.</para>
/// </summary>
public sealed partial class RecordedDraftClient(string draftsDirectory) : IClaudeClient
{
    public Task<ClaudeCompletionResult> CompleteAsync(string systemPrompt, string userMessage, int maxTokens, CancellationToken cancellationToken = default)
    {
        using var json = JsonDocument.Parse(userMessage);
        var slot = json.RootElement.GetProperty("slot").GetString()
                   ?? throw new InvalidOperationException("The user message carries no slot.");

        var file = Directory.EnumerateFiles(draftsDirectory, $"*-{slot}.debug.txt").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault()
                   ?? throw new FileNotFoundException($"No *-{slot}.debug.txt in {draftsDirectory} to replay a draft from.");

        var text = File.ReadAllText(file);
        var draft = RawDraft().Match(text);
        if (!draft.Success)
            throw new InvalidOperationException($"{file} carries no '== MODEL RAW DRAFT' section.");

        return Task.FromResult(new ClaudeCompletionResult(draft.Groups["draft"].Value.Trim(), 0, 0, false));
    }

    [GeneratedRegex(@"== MODEL RAW DRAFT \(before names were bound\) ==\r?\n(?<draft>.*?)\r?\n== FINAL BODY ==", RegexOptions.Singleline)]
    private static partial Regex RawDraft();
}
