namespace AiProxy.Core.Content.Code;

/// <summary>The lexical role of a stretch of source text.</summary>
public enum CodeTokenKind
{
    Other,
    Comment,
    String,
    Identifier,
    Keyword,
    Whitespace,
    NewLine,
}

/// <summary>A source token with its zero-based offset and one-based original line.</summary>
public sealed record CodeToken(CodeTokenKind Kind, string Text, int Offset, int Line, string? ValueText = null);

/// <summary>Tokens for a supported file, or an unsupported result without partial tokens.</summary>
public sealed record CodeLexResult(bool Supported, IReadOnlyList<CodeToken> Tokens);
