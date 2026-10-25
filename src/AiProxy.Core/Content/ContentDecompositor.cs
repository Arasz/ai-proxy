using System.Text;
using System.Text.Json;
using AiProxy.Core.Json;
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
            template.Append(Placeholder(span.Type, chunkId));

            position = span.Start + span.Length;
        }

        template.Append(content, position, content.Length - position);

        return new Decomposition(requestId, content, template.ToString(), parts);
    }

    /// <summary>The placeholder that holds a part's place in the template.</summary>
    /// <param name="type">The kind of the part.</param>
    /// <param name="chunkId">The part's number within the request.</param>
    public static string Placeholder(ContentType type, int chunkId) => $"{{{ShortName(type)}_{chunkId}}}";

    private static string ShortName(ContentType type) =>
        type switch
        {
            ContentType.Prose => "prose",
            ContentType.Log => "log",
            ContentType.Json => "json",
            ContentType.Xml => "xml",
            ContentType.Toml => "toml",
            ContentType.Markdown => "markdown",
            ContentType.SourceCode => "code",
            ContentType.DirectoryListing => "listing",
            ContentType.ShellTranscript => "shell",
            _ => "unknown"
        };

    private List<Span> FindParts(string content)
    {
        var spans = new List<Span>();
        var position = 0;
        var blockStart = -1;
        var blockEnd = 0;
        var failedAttempts = 0;

        while (position < content.Length)
        {
            var lineEnd = content.IndexOf('\n', position);
            var nextLine = lineEnd < 0 ? content.Length : lineEnd + 1;
            var line = content[position..(lineEnd < 0 ? content.Length : lineEnd)];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var fenceEnd = FindFenceEnd(content, nextLine, trimmed[0]);
                if (fenceEnd > 0)
                {
                    Flush(content, spans, ref blockStart, ref blockEnd);
                    spans.Add(new Span(position, fenceEnd - position, ContentType.SourceCode, true));
                    position = fenceEnd;
                    continue;
                }
            }

            if (failedAttempts < MaxFailedStructureAttempts)
            {
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    var valueEnd = FindJsonEnd(content, position);
                    if (valueEnd > position && IsJson(content[position..valueEnd]))
                    {
                        Flush(content, spans, ref blockStart, ref blockEnd);
                        spans.Add(new Span(position, valueEnd - position, ContentType.Json, true));
                        position = valueEnd;
                        continue;
                    }

                    failedAttempts++;
                }

                if (trimmed.StartsWith('<') && !trimmed.StartsWith("<!--", StringComparison.Ordinal))
                {
                    var elementEnd = FindXmlEnd(content, position);
                    if (elementEnd > position && IsXml(content[position..elementEnd]))
                    {
                        Flush(content, spans, ref blockStart, ref blockEnd);
                        spans.Add(new Span(position, elementEnd - position, ContentType.Xml, true));
                        position = elementEnd;
                        continue;
                    }

                    failedAttempts++;
                }
            }

            if (trimmed.Length == 0)
            {
                Flush(content, spans, ref blockStart, ref blockEnd);
                position = nextLine;
                continue;
            }

            if (blockStart < 0)
            {
                blockStart = position;
            }

            blockEnd = lineEnd < 0 ? content.Length : lineEnd;
            position = nextLine;
        }

        Flush(content, spans, ref blockStart, ref blockEnd);

        return Merge(content, spans);
    }

    private static void Flush(string content, List<Span> spans, ref int blockStart, ref int blockEnd)
    {
        if (blockStart < 0)
        {
            return;
        }

        var text = content[blockStart..blockEnd];
        spans.Add(new Span(blockStart, blockEnd - blockStart, Classify(text), false));
        blockStart = -1;
        blockEnd = 0;
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

                if (!previous.Structural && !span.Structural && previous.Type == span.Type && gap.IsWhiteSpace())
                {
                    merged[^1] = previous with { Length = span.Start + span.Length - previous.Start, Structural = false };
                    continue;
                }
            }

            merged.Add(span);
        }

        return merged;
    }

    private static ContentType Classify(string text)
    {
        var lines = text.Split('\n').Where(static line => line.Trim().Length > 0).ToArray();
        if (lines.Length == 0)
        {
            return ContentType.Prose;
        }

        if (lines[0].TrimStart().StartsWith('$'))
        {
            return ContentType.ShellTranscript;
        }

        if (lines.Count(static line => LogLinePattern.IsMatch(line)) >= 2)
        {
            return ContentType.Log;
        }

        if (lines.Count(static line => SearchResultPattern.IsMatch(line)) >= 2)
        {
            return ContentType.SourceCode;
        }

        if (lines.Length >= 2 && lines.Count(static line => PathLinePattern.IsMatch(line) && line.Contains('.', StringComparison.Ordinal)) >= lines.Length * 0.8)
        {
            return ContentType.DirectoryListing;
        }

        if (lines.Length >= 2 && lines.Count(static line => ConfigurationLinePattern.IsMatch(line)) >= lines.Length * 0.8)
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
            var lineEnd = content.IndexOf('\n', position);
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
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = start; index < content.Length; index++)
        {
            var character = content[index];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else
                {
                    switch (character)
                    {
                        case '\\':
                            escaped = true;
                            break;
                        case '"':
                            inString = false;
                            break;
                    }
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    depth++;
                    break;
                case '}':
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return index + 1;
                    }

                    break;
            }
        }

        return -1;
    }

    private static int FindXmlEnd(string content, int start)
    {
        var open = content.IndexOf('>', start);
        if (open < 0)
        {
            return -1;
        }

        if (open > start && content[open - 1] == '/')
        {
            return open + 1;
        }

        var name = ReadTagName(content, start, open);
        if (name.Length == 0)
        {
            return -1;
        }

        var close = content.IndexOf($"</{name}", open, StringComparison.Ordinal);
        if (close < 0)
        {
            return -1;
        }

        var closeEnd = content.IndexOf('>', close);
        return closeEnd < 0 ? -1 : closeEnd + 1;
    }

    private static string ReadTagName(string content, int start, int open)
    {
        var index = start;
        while (index < open && !char.IsWhiteSpace(content[index]) && content[index] != '>')
        {
            index++;
        }

        return content[start..index].TrimStart('<');
    }

    private static bool IsJson(string text)
    {
        using var document = JsonDocument.TryParse(text);
        return document is not null;
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
