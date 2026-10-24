namespace AiProxy.Core.Content.Code;

/// <summary>Conservatively scans C# source for compacted read views.</summary>
public sealed class CSharpCodeLexer : ICodeLexer
{
    private const string KeywordText = "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while add alias and ascending args async await by descending dynamic equals field file from get global group init into join let managed nameof not notnull on or orderby partial record remove required scoped select set unmanaged value var when where with yield extension";

    /// <summary>Whether a spelling is reserved by C# or used as a contextual keyword.</summary>
    public static bool IsKeyword(string text) => (" " + KeywordText + " ").Contains(" " + text + " ", StringComparison.Ordinal);

    /// <inheritdoc />
    public CodeLexResult Lex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<CodeToken>();
        var index = 0;
        var line = 1;
        while (index < text.Length)
        {
            var start = index;
            var tokenLine = line;
            CodeTokenKind kind;
            var current = text[index];
            if (current is '\r' or '\n')
            {
                index += current == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
                line++;
                kind = CodeTokenKind.NewLine;
            }
            else if (current is ' ' or '\t')
            {
                while (index < text.Length && text[index] is ' ' or '\t')
                {
                    index++;
                }
                kind = CodeTokenKind.Whitespace;
            }
            else if (text.AsSpan(index).StartsWith("//", StringComparison.Ordinal))
            {
                index += 2;
                while (index < text.Length && text[index] is not ('\r' or '\n'))
                {
                    index++;
                }
                kind = CodeTokenKind.Comment;
            }
            else if (text.AsSpan(index).StartsWith("/*", StringComparison.Ordinal))
            {
                var close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return Unsupported();
                }
                index = close + 2;
                line += CountNewLines(text.AsSpan(start, index - start));
                kind = CodeTokenKind.Comment;
            }
            else if (current is '"' or '\'' or '@' or '$')
            {
                if (!ReadLiteral(text, ref index))
                {
                    return Unsupported();
                }
                line += CountNewLines(text.AsSpan(start, index - start));
                kind = CodeTokenKind.String;
            }
            else if (char.IsAsciiLetter(current) || current == '_')
            {
                index++;
                while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_'))
                {
                    index++;
                }
                kind = IsKeyword(text[start..index]) ? CodeTokenKind.Keyword : CodeTokenKind.Identifier;
            }
            else
            {
                if (current is '#' or '\\' or '@' or '$' or '\'' || !char.IsAscii(current))
                {
                    return Unsupported();
                }
                index++;
                kind = CodeTokenKind.Other;
            }
            tokens.Add(new CodeToken(kind, text[start..index], start, tokenLine));
        }
        return new CodeLexResult(true, tokens);
    }

    private static bool ReadLiteral(string text, ref int index)
    {
        var verbatim = false;
        var interpolated = false;
        if (text[index] is '@' or '$')
        {
            verbatim = text[index] == '@';
            interpolated = text[index++] == '$';
            if (index < text.Length && text[index] is '@' or '$')
            {
                if (text[index] == (verbatim ? '@' : '$'))
                {
                    return false;
                }
                verbatim = interpolated = true;
                index++;
            }
        }
        if (index >= text.Length || text[index] is not ('"' or '\''))
        {
            return false;
        }
        var quote = text[index];
        if (quote == '\'' && (verbatim || interpolated))
        {
            return false;
        }
        var quoteCount = 0;
        while (index + quoteCount < text.Length && text[index + quoteCount] == '"')
        {
            quoteCount++;
        }
        if (quoteCount >= 3)
        {
            if (verbatim || interpolated)
            {
                return false;
            }
            index += quoteCount;
            var delimiter = new string('"', quoteCount);
            var end = text.IndexOf(delimiter, index, StringComparison.Ordinal);
            if (end < 0 || (end + quoteCount < text.Length && text[end + quoteCount] == '"'))
            {
                return false;
            }
            index = end + quoteCount;
            return true;
        }
        index++;
        var braces = 0;
        var characterLength = 0;
        while (index < text.Length)
        {
            var current = text[index++];
            if (interpolated && braces > 0 && current is '"' or '\'')
            {
                return false;
            }
            if (current == quote)
            {
                if (verbatim && index < text.Length && text[index] == quote)
                {
                    index++;
                    continue;
                }
                return braces == 0 && (quote != '\'' || characterLength == 1);
            }
            if (current is '\r' or '\n' && !verbatim)
            {
                return false;
            }
            if (!verbatim && current == '\\')
            {
                if (index >= text.Length)
                {
                    return false;
                }
                var escaped = text[index++];
                if (escaped is 'u' or 'U' or 'x')
                {
                    var min = escaped == 'x' ? 1 : escaped == 'u' ? 4 : 8;
                    var max = escaped == 'U' ? 8 : 4;
                    var count = 0;
                    while (count < max && index < text.Length && char.IsAsciiHexDigit(text[index]))
                    {
                        index++;
                        count++;
                    }
                    if (count < min)
                    {
                        return false;
                    }
                }
                else if (!"'\"\\0abfnrtv".Contains(escaped, StringComparison.Ordinal))
                {
                    return false;
                }
                characterLength++;
                continue;
            }
            if (interpolated && current is '{' or '}')
            {
                if (braces == 0 && index < text.Length && text[index] == current)
                {
                    index++;
                    continue;
                }
                braces += current == '{' ? 1 : -1;
                if (braces < 0)
                {
                    return false;
                }
            }
            characterLength++;
        }
        return false;
    }

    private static int CountNewLines(ReadOnlySpan<char> text)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n' || (text[index] == '\r' && (index + 1 == text.Length || text[index + 1] != '\n')))
            {
                count++;
            }
        }
        return count;
    }

    private static CodeLexResult Unsupported() => new(false, Array.Empty<CodeToken>());
}
