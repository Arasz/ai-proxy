using System.Collections.Frozen;
using System.Text.RegularExpressions;
using AiProxy.Core.Content.Code;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TreeSitter;

namespace AiProxy.Code;

/// <summary>Scans JavaScript and JSX with the Tree-sitter grammar; template text is string, <c>${}</c> is code.</summary>
public sealed partial class JavaScriptCodeLexer([FromKeyedServices(TreeSitterLanguage.JavaScriptKey)] TreeSitterLanguage language) : ICodeLexer
{
    /// <summary>Reserved words, contextual keywords and globals that change meaning when renamed.</summary>
    internal static readonly FrozenSet<string> Keywords =
    [
        "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else",
        "enum", "export", "extends", "false", "finally", "for", "function", "if", "implements", "import", "in",
        "instanceof", "interface", "let", "new", "null", "package", "private", "protected", "public", "return", "static",
        "super", "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while", "with", "yield",
        "as", "async", "from", "get", "of", "set", "target", "meta", "constructor", "accessor",
        "arguments", "undefined", "NaN", "Infinity", "globalThis", "eval", "require", "module", "exports", "console",
        "window", "document", "process", "global", "__dirname", "__filename",
        "Object", "Array", "Function", "String", "Number", "Boolean", "Symbol", "BigInt", "Math", "JSON", "Date",
        "RegExp", "Error", "TypeError", "RangeError", "SyntaxError", "ReferenceError", "Promise", "Map", "Set",
        "WeakMap", "WeakSet", "Proxy", "Reflect", "parseInt", "parseFloat", "isNaN", "isFinite", "setTimeout",
        "clearTimeout", "setInterval", "clearInterval", "queueMicrotask", "structuredClone", "fetch"
    ];

    private static readonly TreeSitterGrammar Grammar = Script(
        ["identifier", "property_identifier", "shorthand_property_identifier", "shorthand_property_identifier_pattern", "statement_identifier"],
        new Dictionary<string, string?>());

    /// <summary>Creates a lexer on the bundled JavaScript grammar.</summary>
    public JavaScriptCodeLexer()
        : this(new TreeSitterLanguage(TreeSitterLanguage.JavaScriptKey, NullLogger<TreeSitterLanguage>.Instance))
    {
    }

    /// <inheritdoc />
    public bool SupportsPath(string? filePath)
    {
        return filePath is not null
               && (filePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                   || filePath.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
                   || filePath.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)
                   || filePath.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public bool IsKeyword(string text)
    {
        return Keywords.Contains(text);
    }

    /// <inheritdoc />
    public CodeLexResult Lex(string text)
    {
        return TreeSitterScanner.Lex(text, language, Grammar, IsKeyword);
    }

    /// <summary>The JavaScript-family mapping, extended with a dialect's identifier and literal node types.</summary>
    internal static TreeSitterGrammar Script(IEnumerable<string> identifiers, IReadOnlyDictionary<string, string?> strings)
    {
        var literals = new Dictionary<string, string?>(strings)
        {
            ["string"] = null,
            ["template_string"] = "template_substitution",
            ["regex"] = null,
            ["jsx_text"] = null,
            ["html_character_reference"] = null
        };
        var identifierTypes = identifiers.ToFrozenSet();
        return new TreeSitterGrammar(
            literals.ToFrozenDictionary(),
            ["comment", "html_comment"],
            identifierTypes,
            IsJsxName,
            node => identifierTypes.Contains(node.Type) && node.Children.Count == 0 && node.Text.Contains('\\'),
            static node => node.Type == "import_statement" && node.Children.Any(static child => child.Type == "import_clause"),
            true,
            static _ => null,
            static (comment, _) => ToolDirective().IsMatch(comment));
    }

    private static bool IsJsxName(Node node)
    {
        var parent = node.Parent;
        while (parent is { Type: "member_expression" or "nested_identifier" or "jsx_namespace_name" }) parent = parent.Parent;
        return parent is { Type: "jsx_opening_element" or "jsx_closing_element" or "jsx_self_closing_element" or "jsx_attribute" };
    }

    /// <summary>Triple-slash directives, TypeScript and ESLint pragmas, license, source-map and JSX pragma comments.</summary>
    [GeneratedRegex(@"^///\s*<(?:reference|amd-)|@ts-(?:ignore|expect-error|nocheck|check)\b|\beslint-(?:disable|enable)|\bprettier-ignore\b|\bbiome-ignore\b|\b(?:istanbul|c8|v8) ignore\b|^/\*!|^//[#@]\s*sourceMappingURL=|@jsx")]
    private static partial Regex ToolDirective();
}