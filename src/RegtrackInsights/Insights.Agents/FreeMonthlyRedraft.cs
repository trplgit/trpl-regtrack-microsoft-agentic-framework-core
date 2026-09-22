namespace Insights.Agents;

/// <summary>
/// Builds the follow-up user message for a rejected monthly draft: the original input, what the
/// model wrote, and the validator's own failure list.
///
/// <para>The point of a redraft is to make strictness cheap. With one attempt, every rule the
/// validator enforces costs a whole written email whenever the model trips it - which is pressure
/// to delete good rules rather than fix bad drafts. The validator is unchanged and still the only
/// gate: this asks again, it never lowers the bar.</para>
///
/// <para>The failure strings are the validator's, quoted verbatim. They name the offending number,
/// phrase or placeholder, which is exactly what the model needs and nothing more - no suggested
/// wording, because a suggestion is a template and templates are what produced the tour-of-counts
/// emails in the first place.</para>
/// </summary>
public static class FreeMonthlyRedraft
{
    public static string Message(string originalUserMessage, string rejectedDraft, IReadOnlyList<string> failedChecks) =>
        $"""
         {originalUserMessage}

         ---

         YOUR PREVIOUS DRAFT WAS REJECTED. You wrote:

         {rejectedDraft.Trim()}

         It failed these checks:

         {string.Join("\n", failedChecks.Select(f => "- " + f))}

         Write the email again from the same input above. Fix every point listed. Change nothing
         else that was working: keep the same structure and the same findings, and do not replace
         a rejected sentence with a vaguer one - if a figure or a phrase is not allowed, say the
         thing that IS supported by the input, or leave that sentence out entirely.
         """;
}
