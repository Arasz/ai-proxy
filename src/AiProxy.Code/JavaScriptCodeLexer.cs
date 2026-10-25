using System.Collections.Frozen;
using System.Text.RegularExpressions;
using AiProxy.Core.Content.Code;
using TreeSitter;

namespace AiProxy.Code;

/// <summary>Scans JavaScript and JSX with the Tree-sitter grammar; template text is string, <c>${}</c> is code.</summary>
public sealed partial class JavaScriptCodeLexer : ICodeLexer
{
    private static readonly Language Language = new("JavaScript");

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
        "clearTimeout", "setInterval", "clearInterval", "queueMicrotask", "structuredClone", "fetch",
    ];

    private static readonly TreeSitterGrammar Grammar = Script(
        ["identifier", "property_identifier", "shorthand_property_identifier", "shorthand_property_identifier_pattern", "statement_identifier"],
        new Dictionary<string, string?>());

    /// <inheritdoc />
    public bool SupportsPath(string? filePath) =>
        filePath is not null
        && (filePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public bool IsKeyword(string text) => Keywords.Contains(text);

    /// <inheritdoc />
    public CodeLexResult Lex(string text) => TreeSitterScanner.Lex(text, Language, Grammar, IsKeyword);

    /// <summary>The JavaScript-family mapping, extended with a dialect's identifier and literal node types.</summary>
    internal static TreeSitterGrammar Script(IEnumerable<string> identifiers, Dictionary<string, string?> strings)
    {
        strings["string"] = null;
        strings["template_string"] = "template_substitution";
        strings["regex"] = null;
        strings["jsx_text"] = null;
        strings["html_character_reference"] = null;
        var identifierTypes = identifiers.ToFrozenSet();
        return new TreeSitterGrammar(
            strings.ToFrozenDictionary(),
            ["comment", "html_comment"],
            identifierTypes,
            node => identifierTypes.Contains(node.Type) && node.Children.Count == 0 && node.Text.Contains('\\'),
            static node => node.Type == "import_statement" && node.Children.Any(static child => child.Type == "import_clause"),
            true,
            static _ => null,
            static (comment, _) => ToolDirective().IsMatch(comment));
    }

    /// <summary>Triple-slash directives, TypeScript and ESLint pragmas, license, source-map and JSX pragma comments.</summary>
    [GeneratedRegex(@"^///\s*<(?:reference|amd-)|@ts-(?:ignore|expect-error|nocheck|check)\b|\beslint-(?:disable|enable)|^/\*!|^//[#@]\s*sourceMappingURL=|@jsx")]
    private static partial Regex ToolDirective();
}
