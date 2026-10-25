using System.Text;

namespace AiProxy.Core.Content.Code;

/// <summary>Options for the inactive code-view builder; renaming is opt-in.</summary>
public sealed record CodeReductionOptions(bool RemoveComments = true, bool RemoveImports = true, bool RenameIdentifiers = false);

/// <summary>A read view, its change/support flags, and the short-name to original-name map.</summary>
public sealed record CodeReductionResult(string Text, bool Changed, bool Supported, IReadOnlyDictionary<string, string> RenameMap);

/// <summary>Builds compact source read views using an explicit path and caller-rendered retrieval hint.</summary>
public sealed class CodeReducer
{
    private readonly ICodeLexer _lexer;

    /// <summary>Creates a C# view builder with a caller-supplied syntax adapter.</summary>
    public CodeReducer(ICodeLexer lexer)
    {
        ArgumentNullException.ThrowIfNull(lexer);
        _lexer = lexer;
    }

    /// <summary>Returns the original when safety or complete-view savings cannot be established.</summary>
    public CodeReductionResult Reduce(string text, string? filePath, CodeReductionOptions? options, string? retrievalHint)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(retrievalHint) || !_lexer.SupportsPath(filePath))
        {
            return Original(text, false);
        }
        var (source, prefixes) = Unnumber(text);
        var lexed = _lexer.Lex(source, filePath);
        if (!lexed.Supported)
        {
            return Original(text, false);
        }
        options ??= new CodeReductionOptions();
        var view = source.ToCharArray();
        var literals = new bool[source.Length];
        foreach (var token in lexed.Tokens)
        {
            if (token.Kind == CodeTokenKind.String)
            {
                Array.Fill(literals, true, token.Offset, token.Text.Length);
            }
            if (options.RemoveComments && token.Kind == CodeTokenKind.Comment)
            {
                Blank(view, token.Offset, token.Text.Length);
            }
        }
        if (options.RemoveImports && lexed.Imports is { } imports)
        {
            foreach (var span in imports)
            {
                Blank(view, span.Offset, span.Length);
                Array.Fill(literals, false, span.Offset, span.Length);
            }
        }
        else if (options.RemoveImports)
        {
            RemoveImports(view, lexed.Tokens);
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var replacements = options.RenameIdentifiers
            ? Rename(view, lexed.Tokens, map)
            : new Dictionary<int, (int Length, string Text)>();
        var rendered = Render(view, literals, prefixes, replacements);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (rendered.Length > 0 && rendered[^1] is not ('\r' or '\n'))
        {
            rendered += newline;
        }
        foreach (var (shortName, originalName) in map)
        {
            rendered += $"{shortName}:{originalName}{newline}";
        }
        rendered += retrievalHint;
        return rendered.Length < text.Length
            ? new CodeReductionResult(rendered, true, true, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(map))
            : Original(text, true);
    }
    private Dictionary<int, (int Length, string Text)> Rename(
        char[] view, IReadOnlyList<CodeToken> tokens, Dictionary<string, string> map)
    {
        var identifiers = tokens.Where(token => token.Kind == CodeTokenKind.Identifier).ToArray();
        var fixedNames = tokens.Where(static token => token.Kind is CodeTokenKind.Other or CodeTokenKind.Keyword)
            .Select(static token => token.Text)
            .ToHashSet(StringComparer.Ordinal);
        var reserved = identifiers.Select(static token => token.ValueText ?? token.Text).ToHashSet(StringComparer.Ordinal);
        reserved.UnionWith(fixedNames);
        var used = identifiers.Where(token => view.AsSpan(token.Offset, token.Text.Length).SequenceEqual(token.Text));
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var next = 0;
        foreach (var group in used.GroupBy(static token => token.ValueText ?? token.Text, StringComparer.Ordinal))
        {
            if (group.Count() < 2 || _lexer.IsKeyword(group.Key) || fixedNames.Contains(group.Key))
            {
                continue;
            }
            string candidate;
            do
            {
                candidate = ShortName(next++);
            }
            while (reserved.Contains(candidate) || _lexer.IsKeyword(candidate));
            if (candidate.Length >= group.Key.Length)
            {
                continue;
            }
            reserved.Add(candidate);
            names.Add(group.Key, candidate);
            map.Add(candidate, group.Key);
        }
        return used.Where(token => names.ContainsKey(token.ValueText ?? token.Text))
            .ToDictionary(token => token.Offset, token => (token.Text.Length, names[token.ValueText ?? token.Text]));
    }

    private static string ShortName(int value)
    {
        var name = "";
        do
        {
            name = (char)('a' + value % 26) + name;
            value = value / 26 - 1;
        }
        while (value >= 0);
        return name;
    }

    private static void RemoveImports(char[] view, IReadOnlyList<CodeToken> tokens)
    {
        var code = tokens.Where(token => token.Kind is not (CodeTokenKind.Whitespace or CodeTokenKind.NewLine or CodeTokenKind.Comment)).ToArray();
        for (var index = 0; index < code.Length; index++)
        {
            var start = index;
            if (code[index].Text == "global" && index + 1 < code.Length && code[index + 1].Text == "using")
            {
                index++;
            }
            if (code[index].Text != "using" || (start > 0 && code[start - 1].Text is not (";" or "{" or "}")))
            {
                continue;
            }
            var end = index + 1;
            while (end < code.Length && code[end].Text != ";")
            {
                end++;
            }
            if (end == code.Length || !IsImport(code.AsSpan(index + 1, end - index - 1)))
            {
                continue;
            }
            Blank(view, code[start].Offset, code[end].Offset + code[end].Text.Length - code[start].Offset);
            index = end;
        }
    }

    private static bool IsImport(ReadOnlySpan<CodeToken> tokens)
    {
        if (tokens.Length == 0)
        {
            return false;
        }
        if (tokens[0].Text == "static")
        {
            tokens = tokens[1..];
        }
        else if (tokens.Length > 2 && tokens[0].Kind == CodeTokenKind.Identifier && tokens[1].Text == "=")
        {
            tokens = tokens[2..];
        }
        if (tokens.Length >= 2 && tokens[0].Text == "global" && tokens[1].Text == "::")
        {
            tokens = tokens[2..];
        }
        if (tokens.Length == 0 || tokens.Length % 2 == 0)
        {
            return false;
        }
        for (var index = 0; index < tokens.Length; index++)
        {
            if (index % 2 == 0 ? tokens[index].Kind != CodeTokenKind.Identifier : tokens[index].Text is not ("." or "::"))
            {
                return false;
            }
        }
        return true;
    }

    private static (string Source, IReadOnlyList<string> Prefixes) Unnumber(string text)
    {
        var prefixes = new List<string>();
        var source = new StringBuilder();
        var start = 0;
        while (start < text.Length)
        {
            var index = start;
            while (index < text.Length && text[index] == ' ')
            {
                index++;
            }
            var digits = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }
            if (index == digits || index == text.Length || text[index] != '\t')
            {
                return (text, Array.Empty<string>());
            }
            prefixes.Add(text[start..++index]);
            var end = index;
            while (end < text.Length && text[end] is not ('\r' or '\n'))
            {
                end++;
            }
            if (end < text.Length)
            {
                end += text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
            }
            source.Append(text, index, end - index);
            start = end;
        }
        return (source.ToString(), prefixes);
    }

    private static CodeReductionResult Original(string text, bool supported) =>
        new(text, false, supported, new Dictionary<string, string>());

    private static void Blank(char[] text, int start, int length)
    {
        for (var index = start; index < start + length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                text[index] = ' ';
            }
        }
    }

    private static string Render(char[] text, bool[] literals, IReadOnlyList<string> prefixes, IReadOnlyDictionary<int, (int Length, string Text)> replacements)
    {
        var output = new StringBuilder();
        var start = 0;
        var line = 0;
        while (start < text.Length)
        {
            var end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n'))
            {
                end++;
            }
            var next = end;
            if (next < text.Length)
            {
                next += text[next] == '\r' && next + 1 < text.Length && text[next + 1] == '\n' ? 2 : 1;
            }
            var trimmed = end;
            while (trimmed > start && text[trimmed - 1] is ' ' or '\t' && !literals[trimmed - 1])
            {
                trimmed--;
            }
            if (trimmed > start || literals.AsSpan(start, next - start).Contains(true))
            {
                if (prefixes.Count > 0)
                {
                    output.Append(prefixes[line]);
                }
                for (var index = start; index < trimmed; index++)
                {
                    if (replacements.TryGetValue(index, out var replacement))
                    {
                        output.Append(replacement.Text);
                        index += replacement.Length - 1;
                    }
                    else
                    {
                        output.Append(text[index]);
                    }
                }
                output.Append(text, end, next - end);
            }
            start = next;
            line++;
        }
        return output.ToString();
    }
}
