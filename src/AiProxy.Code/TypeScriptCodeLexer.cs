using System.Collections.Frozen;
using AiProxy.Core.Content.Code;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TreeSitter;

namespace AiProxy.Code;

/// <summary>Scans TypeScript with the Tree-sitter grammar; <c>.tsx</c> paths use the TSX grammar.</summary>
public sealed class TypeScriptCodeLexer(
    [FromKeyedServices(TreeSitterLanguage.TypeScriptKey)] TreeSitterLanguage typeScript,
    [FromKeyedServices(TreeSitterLanguage.TsxKey)] TreeSitterLanguage tsx) : ICodeLexer
{
    private static readonly TreeSitterGrammar Grammar = JavaScriptCodeLexer.Script(
        ["identifier", "property_identifier", "shorthand_property_identifier", "shorthand_property_identifier_pattern", "statement_identifier", "type_identifier"],
        new Dictionary<string, string?> { ["template_literal_type"] = "template_type" });

    private static readonly FrozenSet<string> TypeKeywords =
    [
        "any", "unknown", "never", "number", "string", "boolean", "symbol", "bigint", "object", "type", "namespace",
        "declare", "abstract", "readonly", "keyof", "infer", "is", "asserts", "satisfies", "override", "unique", "out",
        "Partial", "Required", "Readonly", "Record", "Pick", "Omit", "Exclude", "Extract", "NonNullable", "ReturnType",
        "Parameters", "ConstructorParameters", "InstanceType", "Awaited", "ThisType", "Uppercase", "Lowercase",
        "Capitalize", "Uncapitalize", "ReadonlyArray", "PromiseLike",
    ];

    /// <summary>Creates a lexer on the bundled TypeScript and TSX grammars.</summary>
    public TypeScriptCodeLexer()
        : this(
            new TreeSitterLanguage(TreeSitterLanguage.TypeScriptKey, NullLogger<TreeSitterLanguage>.Instance),
            new TreeSitterLanguage(TreeSitterLanguage.TsxKey, NullLogger<TreeSitterLanguage>.Instance))
    {
    }

    /// <inheritdoc />
    public bool SupportsPath(string? filePath) =>
        filePath is not null
        && (filePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || IsTsx(filePath));

    /// <summary>JavaScript keywords and globals plus TypeScript type keywords and utility types.</summary>
    public bool IsKeyword(string text) => JavaScriptCodeLexer.Keywords.Contains(text) || TypeKeywords.Contains(text);

    /// <inheritdoc />
    public CodeLexResult Lex(string text) => Lex(text, null);

    /// <inheritdoc />
    public CodeLexResult Lex(string text, string? filePath) =>
        TreeSitterScanner.Lex(text, filePath is not null && IsTsx(filePath) ? tsx : typeScript, Grammar, IsKeyword);

    private static bool IsTsx(string filePath) => filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);
}
