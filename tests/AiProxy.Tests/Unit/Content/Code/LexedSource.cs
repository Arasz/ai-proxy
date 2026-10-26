using AiProxy.Core.Content.Code;

namespace AiProxy.Tests.Unit.Content.Code;

/// <summary>Assertions shared by the language lexer tests.</summary>
internal static class LexedSource
{
    /// <summary>Asserts the tokens cover every character once, in order, with original 1-based lines.</summary>
    public static void AssertCovers(string source, CodeLexResult result)
    {
        Assert.True(result.Supported);
        Assert.Equal(source, string.Concat(result.Tokens.Select(static token => token.Text)));
        var offset = 0;
        var line = 1;
        foreach (var token in result.Tokens)
        {
            Assert.Equal(offset, token.Offset);
            Assert.Equal(line, token.Line);
            Assert.NotEmpty(token.Text);
            line += Lines(token.Text, offset == 0 ? '\0' : source[offset - 1]);
            offset += token.Text.Length;
        }
    }

    /// <summary>The single token of a kind whose text matches exactly.</summary>
    public static CodeToken Single(CodeLexResult result, CodeTokenKind kind, string text)
    {
        return Assert.Single(result.Tokens, token => token.Kind == kind && token.Text == text);
    }

    /// <summary>Every token text of a kind, in source order.</summary>
    public static string[] Texts(CodeLexResult result, CodeTokenKind kind)
    {
        return result.Tokens.Where(token => token.Kind == kind).Select(static token => token.Text).ToArray();
    }

    /// <summary>Distinct identifiers each used twice, enough for two-letter short names.</summary>
    public static IEnumerable<string> ManyNames(int count)
    {
        return Enumerable.Range(0, count).Select(static index => $"name_{index:000}");
    }

    private static int Lines(string text, char previous)
    {
        var lines = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var before = index == 0 ? previous : text[index - 1];
            if (text[index] == '\r' || (text[index] == '\n' && before != '\r'))
            {
                lines++;
            }
        }

        return lines;
    }
}