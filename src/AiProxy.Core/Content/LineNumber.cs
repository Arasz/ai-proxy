namespace AiProxy.Core.Content;

/// <summary>Reads the line number a numbered file read puts before a tab.</summary>
internal static class LineNumber
{
    /// <summary>The number in front of the first tab of a line, after any leading spaces; null when the line has none.</summary>
    /// <param name="line">One line of a tool result.</param>
    public static int? Of(string line)
    {
        var start = 0;
        while (start < line.Length && line[start] == ' ')
        {
            start++;
        }

        var end = start;
        while (end < line.Length && char.IsAsciiDigit(line[end]))
        {
            end++;
        }

        if (end == start || end >= line.Length || line[end] != '\t')
        {
            return null;
        }

        return int.TryParse(line.AsSpan(start, end - start), out var number) ? number : null;
    }
}
