namespace AiProxy.Core.Content.Handlers;

/// <summary>The structural reductions the measured reducer was built from.</summary>
public static class Reduction
{
    /// <summary>Drops a non-blank line that exactly repeats the line before it.</summary>
    /// <remarks>
    ///     Only an adjacent, character-for-character repeat goes. A repeat further down, or one
    ///     that differs in indentation, is a brace, a return or a namespace of source code read
    ///     through a shell, and dropping it rewrites the code the model is shown.
    /// </remarks>
    /// <param name="text">The text to reduce.</param>
    public static string DeduplicateLines(string text)
    {
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                kept.Add(line);
                continue;
            }

            if (kept.Count == 0 || !string.Equals(kept[^1], line, StringComparison.Ordinal))
            {
                kept.Add(line);
            }
        }

        return string.Join("\n", kept);
    }
}