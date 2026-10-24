namespace AiProxy.Core.Content.Code;

/// <summary>Recognizes tokens without modifying their original spelling.</summary>
public interface ICodeLexer
{
    /// <summary>Whether this adapter supports the explicit file path.</summary>
    bool SupportsPath(string? filePath);

    /// <summary>Whether a spelling must be reserved when assigning short names.</summary>
    bool IsKeyword(string text);

    /// <summary>Scans a complete file; unsupported syntax returns no partial tokens.</summary>
    CodeLexResult Lex(string text);
}
