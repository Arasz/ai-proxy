namespace AiProxy.Core.Content.Ccr;

/// <summary>Recognises the two shapes of tool output that are not previewed: a numbered file read and a diff. Line-anchored, never by substring.</summary>
public static class PreviewSkip
{
    /// <summary>How many leading lines of a read must be consecutively numbered.</summary>
    public const int NumberedLines = 5;

    /// <summary>How many leading lines a diff header may sit in.</summary>
    public const int DiffHeaderLines = 50;

    private const string GitHeader = "diff --git ";
    private const string CombinedHeader = "diff --cc ";
    private const string OldFile = "--- ";
    private const string NewFile = "+++ ";
    private const string Hunk = "@@ -";
    private const string CombinedHunk = "@@@ -";

    /// <summary>Whether the text is a file read: its first lines carry consecutive line numbers, from any start number.</summary>
    /// <param name="text">The region text as the tool returned it.</param>
    public static bool IsNumberedRead(string text)
    {
        var lines = FirstLines(text, NumberedLines);
        var count = lines.Count;
        if (count < NumberedLines)
        {
            return false;
        }

        var first = LineNumber.Of(lines[0]);
        if (first is null)
        {
            return false;
        }

        for (var index = 1; index < count; index++)
        {
            if (LineNumber.Of(lines[index]) != first + index)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the text is a diff: a <c>diff --git</c> or <c>diff --cc</c> line, or <c>---</c>, <c>+++</c> and <c>@@ -</c> (or <c>@@@ -</c>) lines in a row, among the first lines.</summary>
    /// <param name="text">The region text as the tool returned it.</param>
    public static bool IsDiff(string text)
    {
        var lines = FirstLines(text, DiffHeaderLines + 2);
        var count = Math.Min(lines.Count, DiffHeaderLines);
        for (var index = 0; index < count; index++)
        {
            if (lines[index].StartsWith(GitHeader, StringComparison.Ordinal) || lines[index].StartsWith(CombinedHeader, StringComparison.Ordinal))
            {
                return true;
            }

            if (index + 2 < lines.Count
                && lines[index].StartsWith(OldFile, StringComparison.Ordinal)
                && lines[index + 1].StartsWith(NewFile, StringComparison.Ordinal)
                && (lines[index + 2].StartsWith(Hunk, StringComparison.Ordinal) || lines[index + 2].StartsWith(CombinedHunk, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> FirstLines(string text, int most)
    {
        var lines = new List<string>(most);
        var start = 0;
        while (lines.Count < most && start <= text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                end = text.Length;
            }

            lines.Add(text[start..end]);
            start = end + 1;
        }

        return lines;
    }
}
