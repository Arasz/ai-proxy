using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AiProxy.Core.Content;

/// <summary>Splits a payload by structure: a fence, a JSON value, an XML element, and the rest.</summary>
/// <remarks>
///     The split is by structure and not by guess, because a guess that cuts a JSON document in
///     half produces two invalid halves, while a structure that is not found leaves the text whole.
///     A block whose kind cannot be established is prose, and a payload with no structure at all
///     stays one part.
/// </remarks>
public sealed partial class ContentDecompositor : IDecompositor
{
    /// <summary>
    ///     How many times the decomposer may look for a structure that is not there.
    /// </summary>
    /// <remarks>
    ///     Looking for a JSON value or an XML element costs a scan to the end of the text,
    ///     because a value that never balances is only known to be unbalanced when the end
    ///     is reached. A payload of lines that each open a value and close none therefore
    ///     costs a scan per line, which is quadratic, and a body of a few megabytes would
    ///     take minutes. A well-formed payload finds its structure on the first try, so a
    ///     small number of failures is enough to tell the two apart: after this many, the
    ///     rest of the text is text.
    /// </remarks>
    private const int MaxFailedStructureAttempts = 32;

    /// <summary>How many lines must look alike before a block is classed by them.</summary>
    private const int MinimumMatchingLines = 2;

    /// <summary>The share of the lines of a block that must look alike for the whole block to be classed by them.</summary>
    private const double MajorityShare = 0.8;

    private static readonly Regex MarkdownLinePattern = CreateMarkdownLinePattern();
    private static readonly Regex PathLinePattern = CreatePathLinePattern();
    private static readonly Regex ConfigurationLinePattern = CreateConfigurationLinePattern();
    private static readonly Regex SearchResultPattern = CreateSearchResultPattern();
    private static readonly Regex LogLinePattern = CreateLogLinePattern();

    /// <inheritdoc />
    public Decomposition Decompose(string content, string requestId, string? filePath = null)
    {
        // Only a whole file named by its extension carries a language; a guessed kind never does.
        var language = content.Trim().Length > 0 ? SourceCodeFiles.LanguageOf(filePath) : null;
        var spans = language is not null
            ? [new Span(0, content.Length, ContentType.SourceCode, true)]
            : FindParts(content);
        var parts = new List<ContentPart>(spans.Count);
        var template = new StringBuilder(content.Length);
        var position = 0;
        var chunkId = 0;

        foreach (var span in spans)
        {
            template.Append(content, position, span.Start - position);
            chunkId++;

            var part = new ContentPart(requestId, chunkId, span.Type, content.Substring(span.Start, span.Length), filePath, language);
            parts.Add(part);
            template.Append(span.Type.Placeholder(chunkId));

            position = span.Start + span.Length;
        }

        template.Append(content, position, content.Length - position);

        return new Decomposition(requestId, content, template.ToString(), parts);
    }

    private static List<Span> FindParts(string content)
    {
        return new PartScanner(content).Scan();
    }

    private static List<Span> Merge(string content, List<Span> spans)
    {
        var merged = new List<Span>(spans.Count);

        foreach (var span in spans)
        {
            if (merged.Count > 0)
            {
                var previous = merged[^1];
                var gap = content.AsSpan(previous.Start + previous.Length, span.Start - previous.Start - previous.Length);

                if (!previous.Structural && !span.Structural && gap.IsWhiteSpace() && MergedType(previous.Type, span.Type) is { } type)
                {
                    merged[^1] = previous with { Length = span.Start + span.Length - previous.Start, Type = type, Structural = false };
                    continue;
                }
            }

            merged.Add(span);
        }

        return merged;
    }

    // Build output takes the plain blocks beside it: the restore and progress lines carry no marker of their own.
    private static ContentType? MergedType(ContentType previous, ContentType next)
    {
        if (previous == next)
        {
            return previous;
        }

        return (previous, next) is (ContentType.BuildOutput, ContentType.Prose) or (ContentType.Prose, ContentType.BuildOutput)
            ? ContentType.BuildOutput
            : null;
    }

    private static ContentType Classify(string text)
    {
        var lines = text.Split(StructureMarkers.NewLine).Where(static line => line.Trim().Length > 0).ToArray();
        if (lines.Length == 0)
        {
            return ContentType.Prose;
        }

        if (lines[0].TrimStart().StartsWith(StructureMarkers.Shell.Prompt))
        {
            return ContentType.ShellTranscript;
        }

        if (lines.Count(static line => LogLinePattern.IsMatch(line)) >= MinimumMatchingLines)
        {
            return ContentType.Log;
        }

        if (lines.Any(static line => MsBuildLines.IsDiagnostic(line) || MsBuildLines.IsSummary(line)))
        {
            return ContentType.BuildOutput;
        }

        if (lines.Count(static line => SearchResultPattern.IsMatch(line)) >= MinimumMatchingLines)
        {
            return ContentType.SourceCode;
        }

        if (lines.Length >= MinimumMatchingLines && lines.Count(static line => PathLinePattern.IsMatch(line) && line.Contains(StructureMarkers.ExtensionSeparator, StringComparison.Ordinal)) >= lines.Length * MajorityShare)
        {
            return ContentType.DirectoryListing;
        }

        if (lines.Length >= MinimumMatchingLines && lines.Count(static line => ConfigurationLinePattern.IsMatch(line)) >= lines.Length * MajorityShare)
        {
            return ContentType.Toml;
        }

        return lines.Any(static line => MarkdownLinePattern.IsMatch(line)) ? ContentType.Markdown : ContentType.Prose;
    }

    private static int FindFenceEnd(string content, int from, char marker)
    {
        var position = from;

        while (position < content.Length)
        {
            var lineEnd = content.IndexOf(StructureMarkers.NewLine, position);
            var line = content[position..(lineEnd < 0 ? content.Length : lineEnd)];
            var trimmed = line.Trim();

            if (trimmed.StartsWith(marker) && trimmed.Trim(marker).Length == 0)
            {
                return lineEnd < 0 ? content.Length : lineEnd + 1;
            }

            if (lineEnd < 0)
            {
                return -1;
            }

            position = lineEnd + 1;
        }

        return -1;
    }

    private static int FindJsonEnd(string content, int start)
    {
        var scanner = new JsonValueScanner();

        for (var index = start; index < content.Length; index++)
        {
            if (scanner.Closes(content[index]))
            {
                return index + 1;
            }
        }

        return -1;
    }

    private static int FindXmlEnd(string content, int start)
    {
        var open = content.IndexOf(StructureMarkers.Xml.TagClose, start);
        if (open < 0)
        {
            return -1;
        }

        if (open > start && content[open - 1] == StructureMarkers.Xml.SelfClosingSlash)
        {
            return open + 1;
        }

        var name = ReadTagName(content, start, open);
        if (name.Length == 0)
        {
            return -1;
        }

        var close = content.IndexOf($"{StructureMarkers.Xml.ClosingTagOpen}{name}", open, StringComparison.Ordinal);
        if (close < 0)
        {
            return -1;
        }

        var closeEnd = content.IndexOf(StructureMarkers.Xml.TagClose, close);
        return closeEnd < 0 ? -1 : closeEnd + 1;
    }

    private static string ReadTagName(string content, int start, int open)
    {
        var index = start;
        while (index < open && !char.IsWhiteSpace(content[index]) && content[index] != StructureMarkers.Xml.TagClose)
        {
            index++;
        }

        return content[start..index].TrimStart(StructureMarkers.Xml.TagOpen);
    }

    private static bool IsXml(string text)
    {
        try
        {
            _ = XElement.Parse(text);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    [GeneratedRegex(
        @"^\s*\d{4}-\d{2}-\d{2}[T ]\S+\s+(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateLogLinePattern();


    [GeneratedRegex(@"^\S+:\d+", RegexOptions.CultureInvariant)]
    private static partial Regex CreateSearchResultPattern();


    [GeneratedRegex(@"^\S+$", RegexOptions.CultureInvariant)]
    private static partial Regex CreatePathLinePattern();


    [GeneratedRegex(@"^\s*[A-Za-z_][A-Za-z0-9_.\-]*\s*=\s*.+$", RegexOptions.CultureInvariant)]
    private static partial Regex CreateConfigurationLinePattern();


    [GeneratedRegex(@"^\s*(#{1,6}\s|[-*+]\s|\d+\.\s|>\s)", RegexOptions.CultureInvariant)]
    private static partial Regex CreateMarkdownLinePattern();

    private readonly record struct Span(int Start, int Length, ContentType Type, bool Structural);
}