using System.Text.RegularExpressions;

namespace AiProxy.Core.Content;

/// <summary>The lines that mark text as MSBuild output: diagnostics and the end-of-build summary.</summary>
public static partial class MsBuildLines
{
    /// <summary>Whether the line is an MSBuild error or warning, such as <c>File.cs(3,5): error CS0029: ...</c>.</summary>
    /// <param name="line">The line to test.</param>
    public static bool IsDiagnostic(string line)
    {
        return DiagnosticPattern().IsMatch(line);
    }

    /// <summary>Whether the line is a result or summary line, such as <c>Build FAILED.</c> or <c>2 Error(s)</c>.</summary>
    /// <param name="line">The line to test.</param>
    public static bool IsSummary(string line)
    {
        return SummaryPattern().IsMatch(line);
    }

    [GeneratedRegex(@"^\s*(\d+(:\d+)?>)?\S.*?(\(\d+(,\d+){0,3}\))?\s?: (error|warning) [A-Za-z]+\d+: ", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticPattern();

    [GeneratedRegex(@"^\s*(Build (succeeded|FAILED)\.|\d+ (Warning|Error)\(s\)|Time Elapsed \d+:\d{2}:\d{2}\.\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryPattern();
}
