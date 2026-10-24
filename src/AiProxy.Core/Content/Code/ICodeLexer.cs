namespace AiProxy.Core.Content.Code;

/// <summary>Recognizes tokens without modifying their original spelling.</summary>
public interface ICodeLexer
{
    /// <summary>Scans a complete file; unsupported syntax returns no partial tokens.</summary>
    CodeLexResult Lex(string text);
}
