using Insights.Domain;

namespace Insights.UnitTests;

/// <summary>
/// The unsubscribe token is the only thing standing between a public URL and anyone being able
/// to unsubscribe every recipient in the estate by incrementing two numbers.
/// </summary>
public sealed class UnsubscribeTokenTests
{
    private const string Key = "a-signing-key";

    [Fact]
    public void Token_VerifiesForTheRecipientItWasIssuedTo()
    {
        var token = UnsubscribeToken.Create(Key, customerId: 23, userId: 357);

        Assert.True(UnsubscribeToken.Verify(Key, 23, 357, token));
    }

    /// <summary>THE POINT OF THE TOKEN. Another recipient's link must not unsubscribe this one.</summary>
    [Theory]
    [InlineData(23, 358)]
    [InlineData(29, 357)]
    public void Token_DoesNotVerifyForADifferentRecipient(int customerId, long userId)
    {
        var token = UnsubscribeToken.Create(Key, customerId: 23, userId: 357);

        Assert.False(UnsubscribeToken.Verify(Key, customerId, userId, token));
    }

    /// <summary>
    /// (1, 23) and (12, 3) must not collide. A separator-free payload would sign "123" for both,
    /// letting one recipient's link unsubscribe an unrelated one.
    /// </summary>
    [Fact]
    public void Token_DistinguishesAmbiguousIdConcatenations()
    {
        Assert.NotEqual(
            UnsubscribeToken.Create(Key, 1, 23),
            UnsubscribeToken.Create(Key, 12, 3));
    }

    [Fact]
    public void Token_DoesNotVerifyUnderADifferentKey()
    {
        var token = UnsubscribeToken.Create(Key, 23, 357);

        Assert.False(UnsubscribeToken.Verify("a-different-key", 23, 357, token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public void Token_RejectsMissingOrGarbageValues(string? token)
    {
        Assert.False(UnsubscribeToken.Verify(Key, 23, 357, token));
    }

    /// <summary>Email clients rewrite query strings; a token containing + or / would not survive.</summary>
    [Fact]
    public void Token_IsUrlSafe()
    {
        var token = UnsubscribeToken.Create(Key, 23, 357);

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    /// <summary>No expiry by design - a recipient may unsubscribe from a six-month-old email.</summary>
    [Fact]
    public void Token_IsStableAcrossCalls()
    {
        Assert.Equal(
            UnsubscribeToken.Create(Key, 23, 357),
            UnsubscribeToken.Create(Key, 23, 357));
    }
}
