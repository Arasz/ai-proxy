using System.Text.RegularExpressions;

namespace AiProxy.Tests.Unit.Cli;

/// <summary>Reads rendered Spectre output the way a terminal shows it: no colour codes, one entry per screen line.</summary>
public static partial class TableLayout
{
    public const int Width = 80;

    public static string[] Lines(string output) => AnsiCodes().Replace(output, "").Split('\n').Select(static line => line.TrimEnd('\r')).ToArray();

    /// <summary>
    /// The text lines of one table, border rules left out: the header line, then every row line up to the line that starts with
    /// <paramref name="after"/>. A cell that wraps adds a line, so the count of this result is the check that nothing wrapped.
    /// </summary>
    public static string[] Table(string[] lines, string firstHeader, string after)
    {
        var header = Array.FindIndex(lines, line => Cells(line)[0] == firstHeader);
        var end = Array.FindIndex(lines, header, line => line.StartsWith(after, StringComparison.Ordinal));
        return lines[header..end].Where(static line => line.Any(char.IsLetterOrDigit)).ToArray();
    }

    /// <summary>The cell texts of one table line, trimmed.</summary>
    public static string[] Cells(string line) => line.Trim('│', ' ').Split('│').Select(static cell => cell.Trim()).ToArray();

    [GeneratedRegex("\u001b\\[[0-9;]*m")]
    private static partial Regex AnsiCodes();
}
