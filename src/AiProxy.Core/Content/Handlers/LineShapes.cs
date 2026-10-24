namespace AiProxy.Core.Content.Handlers;

/// <summary>Shortens the lines that repeat an earlier line's shape to the fields that changed.</summary>
/// <remarks>
/// A line's shape is its space-separated tokens with every value masked: a <c>key=value</c> token
/// keeps its key, and a token holding a digit keeps nothing. The first line of a shape stays whole.
/// A later line of that shape keeps only the tokens that differ from the first, in order, so every
/// value survives and nothing is written that the input did not hold. A later line that differs in
/// no token is dropped.
/// </remarks>
public static class LineShapes
{
    /// <summary>Shortens each line that repeats an earlier line's shape.</summary>
    /// <param name="text">The text to reduce.</param>
    /// <param name="keepWhole">Whether a line is kept whole, and takes no part in shapes.</param>
    public static IReadOnlyList<string> Collapse(string text, Func<string, bool> keepWhole)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(keepWhole);

        var firsts = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var kept = new List<string>();

        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length == 0 || keepWhole(line))
            {
                kept.Add(line);
                continue;
            }

            var tokens = line.Split(' ');
            var shape = Shape(tokens);
            if (!firsts.TryGetValue(shape, out var first))
            {
                firsts.Add(shape, tokens);
                kept.Add(line);
                continue;
            }

            var changed = tokens.Where((token, index) => !string.Equals(token, first[index], StringComparison.Ordinal)).ToArray();
            if (changed.Length > 0)
            {
                kept.Add(string.Join(" ", changed));
            }
        }

        return kept;
    }

    private static string Shape(string[] tokens) => string.Join(" ", tokens.Select(Mask));

    private static string Mask(string token)
    {
        var equals = token.IndexOf('=', StringComparison.Ordinal);
        if (equals > 0)
        {
            return token[..(equals + 1)];
        }

        return token.Any(char.IsAsciiDigit) ? "\0" : token;
    }
}
