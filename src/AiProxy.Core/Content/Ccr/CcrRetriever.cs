using System.Text;
using System.Text.RegularExpressions;
using static AiProxy.Core.Content.Ccr.CcrText;

namespace AiProxy.Core.Content.Ccr;

/// <summary>Answers <c>proxy_retrieve</c> calls against the original kept for a preview id.</summary>
public sealed class CcrRetriever(CcrRetrievalParser parser)
{
    /// <summary>Answers a call from its JSON arguments against the original the caller found for its id.</summary>
    /// <param name="arguments">The tool call's input, as JSON.</param>
    /// <param name="original">The original text kept under the id, or null when none is kept.</param>
    /// <param name="options">The caps the answer keeps to.</param>
    public CcrRetrieval Answer(string arguments, string? original, CcrOptions options)
    {
        var parsed = parser.Parse(arguments, options);
        if (parsed.Request is not { } request)
        {
            return CcrRetrieval.Fail(CcrRetrieval.BadInput, parsed.Reason);
        }

        if (original is null || IdOf(original) != request.Id)
        {
            return CcrRetrieval.Fail(CcrRetrieval.NotKept, Invariant($"No text is kept for {request.Id}. Re-run the tool that produced it."));
        }

        var lines = LinesOf(original);
        var header = Invariant($"[{CcrRetrieval.Name} {request.Id} {request.Mode}: {lines.Length} lines]");

        return request.Mode switch
        {
            RetrievalJson.LinesMode => AnswerLines(header, lines, request.Start!.Value, request.End!.Value, options),
            RetrievalJson.PatternMode => AnswerPattern(header, lines, request.Pattern!, request.Context, options),
            _ => CcrRetrieval.Answered($"{header}\n{original}")
        };
    }

    private static CcrRetrieval AnswerLines(string header, string[] lines, int start, int end, CcrOptions options)
    {
        if (start < 1 || end < start || end > lines.Length)
        {
            return CcrRetrieval.Fail(CcrRetrieval.BadRange, Invariant($"Lines {start}-{end} are outside 1-{lines.Length}; the text has {lines.Length} lines."));
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

        return CcrRetrieval.Answered(text.ToString());
    }

    private static CcrRetrieval AnswerPattern(string header, string[] lines, string pattern, int context, CcrOptions options)
    {
        var regex = Compile(pattern, options);
        if (regex is null)
        {
            return CcrRetrieval.Fail(CcrRetrieval.BadPattern, "The pattern is not a valid .NET regular expression for non-backtracking matching.");
        }

        var scan = FindMatches(regex, lines, options.MaximumMatches);
        if (scan.TimedOutAt is { } line)
        {
            return CcrRetrieval.Fail(CcrRetrieval.BadPattern, Invariant($"The pattern ran longer than {options.PatternTimeout.TotalMilliseconds} ms on line {line + 1}."));
        }

        return scan.Matches.Count == 0
            ? CcrRetrieval.Answered($"{header}\n[no line matches]")
            : CcrRetrieval.Answered(RenderMatches(header, lines, scan, context, options));
    }

    private static Regex? Compile(string pattern, CcrOptions options)
    {
        try
        {
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, options.PatternTimeout);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static MatchScan FindMatches(Regex regex, string[] lines, int maximum)
    {
        var matches = new SortedDictionary<int, Match>();
        var current = 0;
        try
        {
            for (; current < lines.Length; current++)
            {
                var line = lines[current];
                var match = regex.Match(line, 0, line.EndsWith('\r') ? line.Length - 1 : line.Length);
                if (!match.Success)
                {
                    continue;
                }

                if (matches.Count == maximum)
                {
                    return MatchScan.AtLimit(matches);
                }

                matches[current] = match;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return MatchScan.TimedOut(matches, current);
        }

        return MatchScan.Complete(matches);
    }

    private static string RenderMatches(string header, string[] lines, MatchScan scan, int context, CcrOptions options)
    {
        var cap = options.CharactersOf(options.RetrievalTokens);
        var stopNote = Invariant($"\n[stopped after {options.MaximumMatches} matches: narrow the pattern or ask for lines]");
        var text = new StringBuilder(header);
        var last = -1;
        foreach (var index in Shown(scan.Matches.Keys, context, lines.Length))
        {
            var entry = $"{(last >= 0 && index > last + 1 ? "\n--" : "")}\n{Render(index, lines[index], scan.Matches.GetValueOrDefault(index), options.MatchWindow)}";
            var note = Invariant($"\n[stopped at the size cap after line {last + 1}: ask for lines from {last + 2} or narrow the pattern]");
            if (text.Length + entry.Length + Math.Max(note.Length, stopNote.Length) > cap)
            {
                return text.Append(note).ToString();
            }

            text.Append(entry);
            last = index;
        }

        return scan.Stopped ? text.Append(stopNote).ToString() : text.ToString();
    }

    private static IEnumerable<int> Shown(IEnumerable<int> matched, int context, int total)
    {
        var next = 0;
        foreach (var index in matched)
            for (var line = Math.Max(next, index - context); line <= Math.Min(total - 1, index + context); line++)
            {
                yield return line;
                next = line + 1;
            }
    }

    private static string Render(int index, string line, Match? match, int window)
    {
        if (line.Length <= window)
        {
            return Invariant($"{index + 1}\t{line}");
        }

        var start = match is null ? 0 : Math.Clamp(match.Index + match.Length / 2 - window / 2, 0, line.Length - window);
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

    private sealed record MatchScan(SortedDictionary<int, Match> Matches, bool Stopped, int? TimedOutAt)
    {
        public static MatchScan Complete(SortedDictionary<int, Match> matches)
        {
            return new MatchScan(matches, false, null);
        }

        public static MatchScan AtLimit(SortedDictionary<int, Match> matches)
        {
            return new MatchScan(matches, true, null);
        }

        public static MatchScan TimedOut(SortedDictionary<int, Match> matches, int line)
        {
            return new MatchScan(matches, false, line);
        }
    }
}