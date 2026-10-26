using AiProxy.Core.Content;

namespace AiProxy.Tests.Unit.Content.Handlers;

/// <summary>Checks that a reduction only removed characters: what it kept is the input, in order.</summary>
internal static class OrderedSubsequence
{
    /// <summary>Whether every character of <paramref name="candidate" /> appears in <paramref name="source" />, in the same order.</summary>
    public static bool Holds(string candidate, string source)
    {
        var position = 0;
        foreach (var character in candidate)
        {
            position = source.IndexOf(character, position);
            if (position < 0) return false;

            position++;
        }

        return true;
    }

    /// <summary>Fails unless the reduction, without the overlay block it appends, is an ordered subsequence of the input.</summary>
    public static void AssertHolds(string reduced, string source)
    {
        var overlay = reduced.IndexOf(MustKeepOverlay.BlockLabel, StringComparison.Ordinal);
        var body = overlay < 0 ? reduced : reduced[..overlay];

        Assert.True(Holds(body, source), $"The reduction is not an ordered subsequence of its input:{Environment.NewLine}{body}");
    }
}