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

/// <summary>A stretch of source text by zero-based offset and length.</summary>
public readonly record struct CodeSpan(int Offset, int Length);

/// <summary>Tokens for a supported file, or an unsupported result without partial tokens.</summary>
/// <param name="Imports">Import statements the reducer may remove; null applies its C# using-directive rule.</param>
public sealed record CodeLexResult(bool Supported, IReadOnlyList<CodeToken> Tokens, IReadOnlyList<CodeSpan>? Imports = null);
