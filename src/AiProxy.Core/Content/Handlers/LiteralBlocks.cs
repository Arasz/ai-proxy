namespace AiProxy.Core.Content.Handlers;

/// <summary>One stretch of a payload, and whether it is inside a literal block.</summary>
/// <param name="Text">The stretch.</param>
/// <param name="IsLiteral">Whether the stretch is inside a block that is not prose.</param>
public readonly record struct LiteralSegment(string Text, bool IsLiteral);

/// <summary>Finds the blocks a reducer must not reach into.</summary>
/// <remarks>
///     A fenced code block in a Markdown document holds code, and a multi-line string in a
///     configuration file holds a value. Both can contain text that looks like the thing the
///     reducer drops, and dropping it would change what the client sent. The scanner marks
///     those stretches so the reducer can leave them alone.
/// </remarks>
public static class LiteralBlocks
{
    private static readonly string[] TomlDelimiters = ["\"\"\"", "'''"];

    /// <summary>Marks the fenced code blocks of a Markdown document.</summary>
    /// <param name="text">The document to scan.</param>
    public static IReadOnlyList<LiteralSegment> MarkdownFences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var segments = new List<LiteralSegment>();
        var literal = false;

        foreach (var line in SplitLines(text))
        {
            segments.Add(new LiteralSegment(line, literal));

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                literal = !literal;
            }
        }

        return segments;
    }

    /// <summary>Marks the multi-line strings of a configuration file.</summary>
    /// <param name="text">The file to scan.</param>
    public static IReadOnlyList<LiteralSegment> TomlStrings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var segments = new List<LiteralSegment>();
        var inString = false;
        var delimiter = "";

        foreach (var line in SplitLines(text))
        {
            segments.Add(new LiteralSegment(line, inString));

            if (inString)
            {
                if (line.Contains(delimiter, StringComparison.Ordinal))
                {
                    inString = false;
                    delimiter = "";
                }

                continue;
            }

            foreach (var candidate in TomlDelimiters)
            {
                var count = CountOf(line, candidate);
                if (count == 0)
                {
                    continue;
                }

                if (count % 2 == 1)
                {
                    inString = true;
                    delimiter = candidate;
                }

                break;
            }
        }

        return segments;
    }

    /// <summary>Marks the raw strings and the block comments of a source file.</summary>
    /// <param name="text">The file to scan.</param>
    internal static IReadOnlyList<LiteralSegment> SourceCodeLiterals(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var segments = new List<LiteralSegment>();
        var inRawString = false;
        var rawStringDelimiter = "";
        var inBlockComment = false;

        foreach (var line in SplitLines(text))
        {
            var literal = inRawString || inBlockComment;
            segments.Add(new LiteralSegment(line, literal));

            if (inRawString)
            {
                if (CountOf(line, rawStringDelimiter) % 2 == 1)
                {
                    inRawString = false;
                    rawStringDelimiter = "";
                }
            }
            else if (CountOf(line, "\"\"\"") % 2 == 1)
            {
                inRawString = true;
                rawStringDelimiter = "\"\"\"";
            }
            else if (CountOf(line, "'''") % 2 == 1)
            {
                inRawString = true;
                rawStringDelimiter = "'''";
            }

            if (inBlockComment)
            {
                if (line.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = false;
                }
            }
            else if (line.Contains("/*", StringComparison.Ordinal) && !line.Contains("*/", StringComparison.Ordinal))
            {
                inBlockComment = true;
            }
        }

        return segments;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            yield return text[start..(index + 1)];
            start = index + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}