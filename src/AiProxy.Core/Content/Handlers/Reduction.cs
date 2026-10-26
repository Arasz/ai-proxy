namespace AiProxy.Core.Content.Handlers;

/// <summary>The structural reductions the measured reducer was built from.</summary>
public static class Reduction
{
    /// <summary>Keeps the first occurrence of each line and drops the repeats.</summary>
    /// <remarks>
    ///     The measured script said a line repeated on every request is the cheapest byte to
    ///     lose, but its own implementation kept every copy: it zeroed the counter of a line it
    ///     had already kept, and the next copy then read a counter of zero, which is not more
    ///     than one, so the copy was kept as well. This is the behaviour the script described.
    /// </remarks>
    /// <param name="text">The text to reduce.</param>
    public static string DeduplicateLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = text.Split('\n');
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var key = line.Trim();
            if (key.Length == 0)
            {
                kept.Add(line);
                continue;
            }

            if (seen.Add(key)) kept.Add(line);
        }

        return string.Join("\n", kept);
    }
}