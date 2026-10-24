using AiProxy.Core.Content.Code;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiProxy.Code;

/// <summary>Scans C# syntax tokens and trivia with the Roslyn parser.</summary>
public sealed class CSharpCodeLexer : ICodeLexer
{
    private static readonly HashSet<SyntaxKind> StringLiteralKinds =
    [
        SyntaxKind.StringLiteralExpression,
        SyntaxKind.Utf8StringLiteralExpression,
        SyntaxKind.CharacterLiteralExpression,
    ];

    private static readonly HashSet<SyntaxKind> InterpolatedStringTokenKinds =
    [
        SyntaxKind.InterpolatedStringStartToken,
        SyntaxKind.InterpolatedStringEndToken,
        SyntaxKind.InterpolatedVerbatimStringStartToken,
        SyntaxKind.InterpolatedStringToken,
        SyntaxKind.InterpolatedStringTextToken,
        SyntaxKind.InterpolatedSingleLineRawStringStartToken,
        SyntaxKind.InterpolatedMultiLineRawStringStartToken,
        SyntaxKind.InterpolatedRawStringEndToken,
    ];

    /// <inheritdoc />
    public bool SupportsPath(string? filePath) => filePath is not null && filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public bool IsKeyword(string text) =>
        SyntaxFacts.GetKeywordKind(text) != SyntaxKind.None
        || SyntaxFacts.GetContextualKeywordKind(text) != SyntaxKind.None
        || text is "nint" or "nuint" or "value";


    /// <inheritdoc />
    public CodeLexResult Lex(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        if (tree.GetDiagnostics().Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return Unsupported();
        }

        var root = tree.GetRoot();
        var triviaLexingResult = ProcessTriviaTokens(root);
        if (!triviaLexingResult.Supported)
        {
            return triviaLexingResult;
        }

        var tokenLexingResult = ProcessTokens(root);
        if (!tokenLexingResult.Supported)
        {
            return tokenLexingResult;
        }

        var spans = new List<CodeToken>(triviaLexingResult.Tokens.Count + tokenLexingResult.Tokens.Count);
        spans.AddRange(triviaLexingResult.Tokens);
        spans.AddRange(tokenLexingResult.Tokens);
        spans.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        var tokens = CompleteCoverage(text, spans);
        return tokens is null ? Unsupported() : new CodeLexResult(true, tokens);
    }

    private static CodeLexResult ProcessTokens(SyntaxNode root)
    {
        var spans = new List<CodeToken>();
        foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
        {
            if (token.IsKind(SyntaxKind.EndOfFileToken) || token.Span.Length == 0)
            {
                continue;
            }

            var kind = TokenKind(token);
            var valueText = kind == CodeTokenKind.Identifier ? token.ValueText : null;
            spans.Add(new CodeToken(kind, token.Text, token.SpanStart, 1, valueText));
        }

        return new CodeLexResult(true, spans);
    }

    private static CodeLexResult ProcessTriviaTokens(SyntaxNode root)
    {
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

            var triviaSpan = kind == CodeTokenKind.Comment && trivia.HasStructure ? trivia.FullSpan : trivia.Span;
            spans.Add(new CodeToken(kind.Value, trivia.ToFullString(), triviaSpan.Start, 1));
        }

        return new CodeLexResult(true, spans);
    }

    private static CodeTokenKind? TriviaKind(SyntaxKind kind) =>
        kind switch
        {
            SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
                or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia
                => CodeTokenKind.Comment,
            SyntaxKind.WhitespaceTrivia => CodeTokenKind.Whitespace,
            SyntaxKind.EndOfLineTrivia => CodeTokenKind.NewLine,
            _ => null
        };

    private static CodeTokenKind TokenKind(SyntaxToken token)
    {
        var kind = token.Kind();

        if (token.Parent is LiteralExpressionSyntax literal && StringLiteralKinds.Contains(literal.Kind())
            || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
            || token.Parent is InterpolatedStringTextSyntax
            || token.Parent is InterpolatedStringExpressionSyntax && InterpolatedStringTokenKinds.Contains(kind))
        {
            return CodeTokenKind.String;
        }

        if (kind == SyntaxKind.IdentifierToken)
        {
            return CodeTokenKind.Identifier;
        }

        return SyntaxFacts.IsKeywordKind(kind) || SyntaxFacts.IsContextualKeyword(kind)
            ? CodeTokenKind.Keyword
            : CodeTokenKind.Other;
    }

    private static IReadOnlyList<CodeToken>? CompleteCoverage(string text, IReadOnlyList<CodeToken> spans)
    {
        var tokens = new List<CodeToken>(spans.Count);
        var position = 0;
        var scanPosition = 0;
        var line = 1;
        foreach (var codeToken in spans)
        {
            if (codeToken.Offset < position)
            {
                return null;
            }

            if (codeToken.Offset > position)
            {
                var gap = text[position..codeToken.Offset];
                tokens.Add(new CodeToken(CodeTokenKind.Other, gap, position, line));
                line = AdvanceLines(text, ref scanPosition, codeToken.Offset, line);
            }

            tokens.Add(codeToken with { Line = line });
            position = codeToken.Offset + codeToken.Text.Length;
            line = AdvanceLines(text, ref scanPosition, position, line);
        }

        if (position < text.Length)
        {
            var gap = text[position..];
            tokens.Add(new CodeToken(CodeTokenKind.Other, gap, position, line));
        }

        return tokens;
    }

    private static int AdvanceLines(string text, ref int scanPosition, int end, int line)
    {
        while (scanPosition < end)
        {
            var current = text[scanPosition];
            if (current is '\r' or '\u0085' or '\u2028' or '\u2029'
                || current == '\n' && (scanPosition == 0 || text[scanPosition - 1] != '\r'))
            {
                line++;
            }

            scanPosition++;
        }

        return line;
    }

    private static CodeLexResult Unsupported() => new(false, []);
}
