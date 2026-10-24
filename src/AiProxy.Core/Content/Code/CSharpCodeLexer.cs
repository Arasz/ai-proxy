using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiProxy.Core.Content.Code;

/// <summary>Scans C# syntax tokens and trivia with the Roslyn parser.</summary>
public sealed class CSharpCodeLexer : ICodeLexer
{
    /// <summary>Whether a spelling is reserved by C# or used as a contextual keyword.</summary>
    public static bool IsKeyword(string text) =>
        SyntaxFacts.GetKeywordKind(text) != SyntaxKind.None
        || SyntaxFacts.GetContextualKeywordKind(text) != SyntaxKind.None
        || text is "nint" or "nuint";

    /// <inheritdoc />
    public CodeLexResult Lex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tree = CSharpSyntaxTree.ParseText(text);
        if (tree.GetDiagnostics().Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return Unsupported();
        }

        var root = tree.GetRoot();
        var spans = new List<CodeToken>();

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: false))
        {
            if (trivia.Kind() is SyntaxKind.DisabledTextTrivia or SyntaxKind.SkippedTokensTrivia
                || trivia.HasStructure && trivia.GetStructure() is DirectiveTriviaSyntax)
            {
                return Unsupported();
            }

            var kind = TriviaKind(trivia.Kind());
            if (kind is null || trivia.Span.Length == 0)
            {
                continue;
            }

            spans.Add(new CodeToken(kind.Value, trivia.ToFullString(), trivia.SpanStart, LineAt(text, trivia.SpanStart)));
        }

        foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
        {
            if (token.IsKind(SyntaxKind.EndOfFileToken) || token.Span.Length == 0)
            {
                continue;
            }

            var kind = TokenKind(token);
            var valueText = kind == CodeTokenKind.Identifier ? token.ValueText : null;
            spans.Add(new CodeToken(kind, token.Text, token.SpanStart, LineAt(text, token.SpanStart), valueText));
        }

        spans.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        var tokens = CompleteCoverage(text, spans);
        return tokens is null ? Unsupported() : new CodeLexResult(true, tokens);
    }

    private static CodeTokenKind? TriviaKind(SyntaxKind kind) => kind switch
    {
        SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
            or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia
            => CodeTokenKind.Comment,
        SyntaxKind.WhitespaceTrivia => CodeTokenKind.Whitespace,
        SyntaxKind.EndOfLineTrivia => CodeTokenKind.NewLine,
        _ => null,
    };

    private static CodeTokenKind TokenKind(SyntaxToken token)
    {
        if (token.Parent is LiteralExpressionSyntax literal
            && literal.Kind() is SyntaxKind.StringLiteralExpression
                or SyntaxKind.Utf8StringLiteralExpression
                or SyntaxKind.CharacterLiteralExpression)
        {
            return CodeTokenKind.String;
        }

        if (token.Parent is InterpolatedStringTextSyntax
            || token.Parent is InterpolatedStringExpressionSyntax
                && token.Kind().ToString().StartsWith("Interpolated", StringComparison.Ordinal))
        {
            return CodeTokenKind.String;
        }

        var kind = token.Kind();
        if (kind == SyntaxKind.IdentifierToken)
        {
            return SyntaxFacts.GetContextualKeywordKind(token.ValueText) == SyntaxKind.None
                || token.Text.StartsWith('@')
                ? CodeTokenKind.Identifier
                : CodeTokenKind.Keyword;
        }

        return SyntaxFacts.IsKeywordKind(kind) || SyntaxFacts.IsContextualKeyword(kind)
            ? CodeTokenKind.Keyword
            : CodeTokenKind.Other;
    }

    private static IReadOnlyList<CodeToken>? CompleteCoverage(string text, IReadOnlyList<CodeToken> spans)
    {
        var tokens = new List<CodeToken>(spans.Count);
        var position = 0;
        foreach (var span in spans)
        {
            if (span.Offset < position)
            {
                return null;
            }

            if (span.Offset > position)
            {
                var gap = text[position..span.Offset];
                tokens.Add(new CodeToken(CodeTokenKind.Other, gap, position, LineAt(text, position)));
            }

            tokens.Add(span);
            position = span.Offset + span.Text.Length;
        }

        if (position < text.Length)
        {
            var gap = text[position..];
            tokens.Add(new CodeToken(CodeTokenKind.Other, gap, position, LineAt(text, position)));
        }

        return tokens;
    }

    private static int LineAt(string text, int end)
    {
        var line = 1;
        for (var index = 0; index < end; index++)
        {
            if (text[index] is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029')
            {
                if (text[index] == '\r' && index + 1 < end && text[index + 1] == '\n')
                {
                    index++;
                }

                line++;
            }
        }

        return line;
    }

    private static CodeLexResult Unsupported() => new(false, Array.Empty<CodeToken>());
}
