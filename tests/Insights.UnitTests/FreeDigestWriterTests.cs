using Insights.Agents;
using Xunit;

namespace Insights.UnitTests;

/// <summary>
/// [REGRESSION] CompletionTokenBudget exists because tokenCap (the TOTAL prompt+completion
/// budget) used to be passed straight through as the provider's max_tokens (an OUTPUT-only
/// limit) - handing the model room to write up to the WHOLE budget on top of a system prompt
/// that already consumed most of it. Confirmed live: 6/6 LLM calls for one tenant were accepted
/// by the API, then discarded by the post-hoc total-budget check every single time - real LLM
/// spend for zero shipped output. These tests pin the pure sizing function directly (no network
/// call needed) so this class of "the cap was real but the request never had a chance of fitting
/// it" bug cannot recur silently.
/// </summary>
public sealed class FreeDigestWriterTests
{
    /// <summary>~4 chars/token, matching the class's own documented ratio - keeps these tests self-consistent with the implementation without hardcoding a duplicate ratio.</summary>
    private static string CharsOfLength(int approxTokens) => new('a', approxTokens * 4);

    [Fact]
    public void CompletionTokenBudget_SubtractsEstimatedPromptCostFromTheTotalCap()
    {
        var systemPrompt = CharsOfLength(1000);
        var userMessage = CharsOfLength(100);

        var budget = FreeDigestWriter.CompletionTokenBudget(systemPrompt, userMessage, tokenCap: 2100);

        // 2100 - (1000 + 100) = 1000, clamped down to the max (600) - never handed more room
        // than the email could plausibly use, even when the total cap has room to spare.
        Assert.Equal(600, budget);
    }

    [Fact]
    public void CompletionTokenBudget_NeverExceedsMaxCompletionTokens_EvenWithASmallPrompt()
    {
        var budget = FreeDigestWriter.CompletionTokenBudget(systemPrompt: "tiny", userMessage: "tiny", tokenCap: 100_000);

        Assert.Equal(600, budget);
    }

    [Fact]
    public void CompletionTokenBudget_NeverDropsBelowMinCompletionTokens_EvenWhenThePromptAloneExceedsTheCap()
    {
        // The real-world case that caused this bug: a ~1400-token prompt against a 1500 cap
        // leaves ~100 tokens of "room" - clamped UP to the floor so the model still gets a fair
        // shot rather than a request so small it is guaranteed to truncate. The post-hoc
        // totalTokens check in WriteAsync remains the authoritative guard if this still goes over.
        var systemPrompt = CharsOfLength(1400);
        var userMessage = CharsOfLength(60);

        var budget = FreeDigestWriter.CompletionTokenBudget(systemPrompt, userMessage, tokenCap: 1500);

        Assert.Equal(150, budget);
    }

    [Fact]
    public void CompletionTokenBudget_GivesRealRoomOnceTheCapIsRecalibratedToThePrompt_size()
    {
        // The actual fix deployed: Budget:FreeDigestTokenCap raised from 1500 to 2100 to match
        // the real prompt cost (~1400-1460 tokens) plus a genuine completion allowance. This
        // proves that recalibration, not just the dynamic-sizing logic, is what makes a real
        // ~250-400 token completion request possible instead of getting clamped to the floor.
        var systemPrompt = CharsOfLength(1440);
        var userMessage = CharsOfLength(60);

        var budget = FreeDigestWriter.CompletionTokenBudget(systemPrompt, userMessage, tokenCap: 2100);

        Assert.True(budget >= 400, $"expected at least 400 tokens of real completion room, got {budget}");
    }
}
