namespace Insights.Agents;

/// <summary>A model draft after the deterministic layers, and the validator's verdict on it.</summary>
/// <param name="Body">Normalised, repaired and arranged - still in placeholder form.</param>
public sealed record PreparedDraft(string Body, FreeMonthlyReview Review, IReadOnlyList<string> Removed);

/// <summary>
/// The deterministic layers every monthly model draft goes through before anything else looks at
/// it: emphasis, then sentence repair, then paragraph order, then the validator. One place, so the
/// first draft and any validator redraft are held to exactly the same bar.
/// </summary>
public static class FreeMonthlyDraftPipeline
{
    public static PreparedDraft Prepare(string rawBody, FreeMonthlyDigestPrompt prompt)
    {
        /*  Presentation is fixed, never rejected: normalise emphasis, then delete the sentences
            that comment on the figures instead of stating them. Only then is what remains
            checked for truth. Deleting can never add a claim, so the order is safe.        */
        var headlineFigure = prompt.HeadlineMarker is { } marker && !marker.StartsWith("{{", StringComparison.Ordinal) ? marker : null;
        var normalized = FreeMonthlyDraftNormalizer.Normalize(rawBody, headlineFigure);
        var repaired = FreeMonthlyDraftRepair.Apply(normalized, prompt);

        /*  [OWNER, 2026-09-24] Paragraphs of the same period sit together: lead, then last
            month, this month so far, the standing position, what is still to come. Only the
            order of whole paragraphs changes, so what was true before is true after.      */
        var body = FreeMonthlyParagraphOrder.Arrange(repaired.Body);

        return new PreparedDraft(body, FreeMonthlyDigestValidator.Validate(body, prompt), repaired.Removed);
    }
}
