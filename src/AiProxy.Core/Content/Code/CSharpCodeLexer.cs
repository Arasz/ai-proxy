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

            var triviaSpan = kind == CodeTokenKind.Comment && trivia.HasStructure ? trivia.FullSpan : trivia.Span;
            spans.Add(new CodeToken(kind.Value, trivia.ToFullString(), triviaSpan.Start, 1));
        }

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
            var contextualKind = SyntaxFacts.GetContextualKeywordKind(token.ValueText);
            if (contextualKind == SyntaxKind.None || token.Text.StartsWith('@'))
            {
                return CodeTokenKind.Identifier;
            }

            if (IsContextualKeywordUse(token, contextualKind))
            {
                return CodeTokenKind.Keyword;
            }

            return token.Parent is IdentifierNameSyntax or VariableDeclaratorSyntax or ParameterSyntax
                ? CodeTokenKind.Identifier
                : CodeTokenKind.Other;
        }

        return SyntaxFacts.IsKeywordKind(kind) || SyntaxFacts.IsContextualKeyword(kind)
            ? CodeTokenKind.Keyword
            : CodeTokenKind.Other;
    }

    private static bool IsContextualKeywordUse(SyntaxToken token, SyntaxKind contextualKind) => contextualKind switch
    {
        SyntaxKind.VarKeyword => token.Parent is IdentifierNameSyntax { Parent: VariableDeclarationSyntax or ForEachStatementSyntax or DeclarationExpressionSyntax },
        SyntaxKind.RecordKeyword => token.Parent is RecordDeclarationSyntax,
        SyntaxKind.AwaitKeyword => token.Parent is AwaitExpressionSyntax,
        SyntaxKind.YieldKeyword => token.Parent is YieldStatementSyntax,
        SyntaxKind.WithKeyword => token.Parent is WithExpressionSyntax,
        SyntaxKind.InitKeyword => token.Parent is AccessorDeclarationSyntax accessor && accessor.IsKind(SyntaxKind.InitAccessorDeclaration),
        _ => false,
    };

    private static IReadOnlyList<CodeToken>? CompleteCoverage(string text, IReadOnlyList<CodeToken> spans)
    {
        var tokens = new List<CodeToken>(spans.Count);
        var position = 0;
        var scanPosition = 0;
        var line = 1;
        foreach (var span in spans)
        {
            if (span.Offset < position)
            {
                return null;
            }

            if (span.Offset > position)
            {
                var gap = text[position..span.Offset];
                tokens.Add(new CodeToken(CodeTokenKind.Other, gap, position, line));
                line = AdvanceLines(text, ref scanPosition, span.Offset, line);
            }

            tokens.Add(span with { Line = line });
            position = span.Offset + span.Text.Length;
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

    private static CodeLexResult Unsupported() => new(false, Array.Empty<CodeToken>());
}
