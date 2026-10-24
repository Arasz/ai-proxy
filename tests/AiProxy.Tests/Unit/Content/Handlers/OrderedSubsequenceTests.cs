namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>The subsequence helper, stated as behaviour, so the reducer tests that lean on it can fail.</summary>
public sealed class OrderedSubsequenceTests
{
    [Fact]
    public void Dropped_characters_keep_the_subsequence()
    {
        Assert.True(OrderedSubsequence.Holds("ac", "abc"));
    }

    [Fact]
    public void An_inserted_character_breaks_the_subsequence()
    {
        Assert.False(OrderedSubsequence.Holds("abXc", "abc"));
    }

    [Fact]
    public void A_swapped_order_breaks_the_subsequence()
    {
        Assert.False(OrderedSubsequence.Holds("ca", "abc"));
    }

    [Fact]
    public void The_overlay_block_is_excused()
    {
        OrderedSubsequence.AssertHolds("ac\n[retained identifiers] zzz", "abc");
    }
}
