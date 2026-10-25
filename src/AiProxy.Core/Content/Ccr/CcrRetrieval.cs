using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiProxy.Core.Content.Ccr;

/// <summary>The answer to one <c>proxy_retrieve</c> call: the result text, and an error code when it failed.</summary>
/// <param name="Text">The tool result the model reads.</param>
/// <param name="Error">One of the error codes, or null on success; a zero-match pattern is a success.</param>
public sealed record CcrRetrieval(string Text, string? Error)
{
    /// <summary>The tool's name.</summary>
    public const string Name = "proxy_retrieve";

    /// <summary>The tool definition appended to a request's tools; its bytes never change within a release.</summary>
    public const string Definition = """{"name":"proxy_retrieve","description":"Reads part of a tool result the proxy shortened. Use the id from a [proxy preview ...] line. Ask for lines or a pattern; ask for the whole text only when you need all of it.","input_schema":{"type":"object","properties":{"id":{"type":"string","pattern":"^[0-9a-f]{16}$"},"mode":{"type":"string","enum":["lines","pattern","whole"]},"start":{"type":"integer","minimum":1},"end":{"type":"integer","minimum":1},"pattern":{"type":"string","description":".NET regular expression, case-sensitive"},"context":{"type":"integer","minimum":0,"maximum":10}},"required":["id","mode"],"additionalProperties":false}}""";

    /// <summary>The id has no original: unknown, evicted or never kept.</summary>
    public const string NotKept = "not_kept";

    /// <summary>The line range is outside the original.</summary>
    public const string BadRange = "bad_range";

    /// <summary>The pattern is not a valid non-backtracking .NET regular expression, or it timed out.</summary>
    public const string BadPattern = "bad_pattern";

    /// <summary>The arguments do not follow the tool's schema.</summary>
    public const string BadInput = "bad_input";

    private static readonly string[] Properties = ["id", "mode", "start", "end", "pattern", "context"];

    /// <summary>Answers a call from its JSON arguments against the original the caller found for its id.</summary>
    /// <param name="arguments">The tool call's input, as JSON.</param>
    /// <param name="original">The original text kept under the id, or null when none is kept.</param>
    /// <param name="options">The caps the answer keeps to.</param>
    public static CcrRetrieval Answer(string arguments, string? original, CcrOptions options)
    {
        var (request, reason) = Parse(arguments, options);
        if (request is null)
        {
            return Fail(BadInput, reason);
        }

        if (original is null || CcrPreview.IdOf(original) != request.Id)
        {
            return Fail(NotKept, Invariant($"No text is kept for {request.Id}. Re-run the tool that produced it."));
        }

        var lines = CcrPreview.LinesOf(original);
        var header = Invariant($"[{Name} {request.Id} {request.Mode}: {lines.Length} lines]");

        return request.Mode switch
        {
            "lines" => AnswerLines(header, lines, request.Start!.Value, request.End!.Value, options),
            "pattern" => AnswerPattern(header, lines, request.Pattern!, request.Context, options),
            _ => new CcrRetrieval(header + "\n" + original, null)
        };
    }

    private static CcrRetrieval AnswerLines(string header, string[] lines, int start, int end, CcrOptions options)
    {
        if (start < 1 || end < start || end > lines.Length)
        {
            return Fail(BadRange, Invariant($"Lines {start}-{end} are outside 1-{lines.Length}; the text has {lines.Length} lines."));
        }

        var cap = options.CharactersOf(options.RetrievalTokens);
        var text = new StringBuilder(header);
        for (var number = start; number <= end; number++)
        {
            var entry = Invariant($"\n{number}\t{lines[number - 1]}");
            var note = number == start
                ? Invariant($"\n[line {number} is longer than the size cap: ask for a pattern or the whole text]")
                : Invariant($"\n[stopped at the size cap: call again with start {number}]");
            if (text.Length + entry.Length + note.Length > cap)
            {
                text.Append(note);
                break;
            }

            text.Append(entry);
        }

        return new CcrRetrieval(text.ToString(), null);
    }

    private static CcrRetrieval AnswerPattern(string header, string[] lines, string pattern, int context, CcrOptions options)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, options.PatternTimeout);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Fail(BadPattern, "The pattern is not a valid .NET regular expression for non-backtracking matching.");
        }

        var matches = new SortedDictionary<int, Match>();
        var stopped = false;
        var current = 0;
        try
        {
            for (; current < lines.Length; current++)
            {
                var match = regex.Match(lines[current]);
                if (!match.Success)
                {
                    continue;
                }

                if (matches.Count == options.MaximumMatches)
                {
                    stopped = true;
                    break;
                }

                matches[current] = match;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Fail(BadPattern, Invariant($"The pattern ran longer than {options.PatternTimeout.TotalMilliseconds} ms on line {current + 1}."));
        }

        if (matches.Count == 0)
        {
            return new CcrRetrieval(header + "\n[no line matches]", null);
        }

        var cap = options.CharactersOf(options.RetrievalTokens);
        var stopNote = Invariant($"\n[stopped after {options.MaximumMatches} matches: narrow the pattern or ask for lines]");
        var text = new StringBuilder(header);
        var last = -1;
        foreach (var index in Shown(matches.Keys, context, lines.Length))
        {
            var entry = (last >= 0 && index > last + 1 ? "\n--" : "") + "\n" + Render(index, lines[index], matches.GetValueOrDefault(index), options.MatchWindow);
            var note = Invariant($"\n[stopped at the size cap after line {last + 1}: ask for lines from {last + 2} or narrow the pattern]");
            if (text.Length + entry.Length + Math.Max(note.Length, stopNote.Length) > cap)
            {
                text.Append(note);
                return new CcrRetrieval(text.ToString(), null);
            }

            text.Append(entry);
            last = index;
        }

        if (stopped)
        {
            text.Append(stopNote);
        }

        return new CcrRetrieval(text.ToString(), null);
    }

    private static IEnumerable<int> Shown(IEnumerable<int> matched, int context, int total)
    {
        var next = 0;
        foreach (var index in matched)
        {
            for (var line = Math.Max(next, index - context); line <= Math.Min(total - 1, index + context); line++)
            {
                yield return line;
                next = line + 1;
            }
        }
    }

    private static string Render(int index, string line, Match? match, int window)
    {
        if (line.Length <= window)
        {
            return Invariant($"{index + 1}\t{line}");
        }

        var start = match is null ? 0 : Math.Clamp(match.Index + (match.Length / 2) - (window / 2), 0, line.Length - window);
        var end = start + window;
        if (start > 0 && char.IsLowSurrogate(line[start]) && char.IsHighSurrogate(line[start - 1]))
        {
            start++;
        }

        if (end < line.Length && char.IsHighSurrogate(line[end - 1]) && char.IsLowSurrogate(line[end]))
        {
            end--;
        }

        return Invariant($"{index + 1}\t[characters {start + 1}-{end} of {line.Length}] {line[start..end]}");
    }

    private static (Request? Request, string Reason) Parse(string arguments, CcrOptions options)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(arguments);
        }
        catch (JsonException)
        {
            return (null, "The arguments are not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "The arguments are not a JSON object.");
            }

            foreach (var property in root.EnumerateObject())
            {
                string name;
                try
                {
                    name = property.Name;
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        _ = property.Value.GetString();
                    }
                }
                catch (InvalidOperationException)
                {
                    return (null, "The arguments contain malformed Unicode.");
                }

                if (!Properties.Contains(name, StringComparer.Ordinal))
                {
                    return (null, $"Unknown property '{name}'.");
                }
            }

            var id = String(root, "id");
            if (id is null || !IsId(id))
            {
                return (null, "'id' must be the 16 lowercase hex characters from a [proxy preview ...] line.");
            }

            var mode = String(root, "mode");
            if (mode is not ("lines" or "pattern" or "whole"))
            {
                return (null, "'mode' must be lines, pattern or whole.");
            }

            var (start, startValid) = Integer(root, "start");
            var (end, endValid) = Integer(root, "end");
            var (context, contextValid) = Integer(root, "context");
            var pattern = String(root, "pattern");
            if (!startValid || !endValid || !contextValid || (root.TryGetProperty("pattern", out _) && pattern is null))
            {
                return (null, "'start', 'end' and 'context' must be integers and 'pattern' a string.");
            }

            if (context is < 0 || context > options.MaximumContext)
            {
                return (null, Invariant($"'context' must be 0 to {options.MaximumContext}."));
            }

            if (mode == "lines" && (start is null || end is null))
            {
                return (null, "Mode lines needs 'start' and 'end'.");
            }

            if (mode == "pattern" && pattern is null)
            {
                return (null, "Mode pattern needs 'pattern'.");
            }

            return (new Request(id, mode, start, end, pattern, context ?? 0), "");
        }
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (int? Value, bool Valid) Integer(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return (null, true);
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? (number, true) : (null, false);
    }

    private static bool IsId(string id) => id.Length == 16 && id.All(char.IsAsciiHexDigitLower);

    private static CcrRetrieval Fail(string code, string reason) => new(Invariant($"[{Name} error {code}] {reason}"), code);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private sealed record Request(string Id, string Mode, int? Start, int? End, string? Pattern, int Context);
}
