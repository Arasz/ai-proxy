using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using AiProxy.Core.Content.Code;
using TreeSitter;

namespace AiProxy.Code;

/// <summary>Scans Python with the Tree-sitter grammar; docstrings are strings and f-string fields are code.</summary>
public sealed partial class PythonCodeLexer(TreeSitterLanguage language) : ICodeLexer
{
    private static readonly TreeSitterLanguage Python = new("Python");

    private static readonly TreeSitterGrammar Grammar = new(
        Strings: new Dictionary<string, string?> { ["string"] = "interpolation", ["format_specifier"] = "format_expression" }.ToFrozenDictionary(),
        Comments: ["comment"],
        Identifiers: ["identifier"],
        IsFixedName: static _ => false,
        IsUnsupported: static node => node.Type == "interpolation" && node.Children.Any(static child => !child.IsNamed && child.Type == "="),
        IsImport: static node => node.Type is "import_statement" or "import_from_statement",
        ImportNeedsTerminator: false,
        ValueText: static text => text.IsNormalized(NormalizationForm.FormKC) ? null : text.Normalize(NormalizationForm.FormKC),
        IsDirective: static (comment, line) => line == 1 && comment.StartsWith("#!", StringComparison.Ordinal)
            || line <= 2 && EncodingCookie().IsMatch(comment)
            || ToolDirective().IsMatch(comment));

    private static readonly FrozenSet<string> Keywords =
    [
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
        "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal",
        "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
        "_", "case", "lazy", "match", "type",
        "self", "cls",
        "ArithmeticError", "AssertionError", "AttributeError", "BaseException", "BaseExceptionGroup", "BlockingIOError",
        "BrokenPipeError", "BufferError", "BytesWarning", "ChildProcessError", "ConnectionAbortedError", "ConnectionError",
        "ConnectionRefusedError", "ConnectionResetError", "DeprecationWarning", "EOFError", "Ellipsis", "EncodingWarning",
        "EnvironmentError", "Exception", "ExceptionGroup", "FileExistsError", "FileNotFoundError", "FloatingPointError",
        "FutureWarning", "GeneratorExit", "IOError", "ImportError", "ImportWarning", "IndentationError", "IndexError",
        "InterruptedError", "IsADirectoryError", "KeyError", "KeyboardInterrupt", "LookupError", "MemoryError",
        "ModuleNotFoundError", "NameError", "NotADirectoryError", "NotImplemented", "NotImplementedError", "OSError",
        "OverflowError", "PendingDeprecationWarning", "PermissionError", "ProcessLookupError", "RecursionError",
        "ReferenceError", "ResourceWarning", "RuntimeError", "RuntimeWarning", "StopAsyncIteration", "StopIteration",
        "SyntaxError", "SyntaxWarning", "SystemError", "SystemExit", "TabError", "TimeoutError", "TypeError",
        "UnboundLocalError", "UnicodeDecodeError", "UnicodeEncodeError", "UnicodeError", "UnicodeTranslateError",
        "UnicodeWarning", "UserWarning", "ValueError", "Warning", "ZeroDivisionError",
        "abs", "aiter", "all", "anext", "any", "ascii", "bin", "bool", "breakpoint", "bytearray", "bytes", "callable",
        "chr", "classmethod", "compile", "complex", "delattr", "dict", "dir", "divmod", "enumerate", "eval", "exec",
        "exit", "filter", "float", "format", "frozenset", "getattr", "globals", "hasattr", "hash", "help", "hex", "id",
        "input", "int", "isinstance", "issubclass", "iter", "len", "list", "locals", "map", "max", "memoryview", "min",
        "next", "object", "oct", "open", "ord", "pow", "print", "property", "quit", "range", "repr", "reversed", "round",
        "set", "setattr", "slice", "sorted", "staticmethod", "str", "sum", "super", "tuple", "vars", "zip",
    ];

    /// <summary>Creates a lexer on the bundled Python grammar.</summary>
    public PythonCodeLexer()
        : this(Python)
    {
    }

    /// <inheritdoc />
    public bool SupportsPath(string? filePath) => filePath is not null && filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    /// <summary>Keywords, soft keywords, builtins, <c>self</c>/<c>cls</c> and dunder names.</summary>
    public bool IsKeyword(string text) =>
        Keywords.Contains(text) || text.Length > 4 && text.StartsWith("__", StringComparison.Ordinal) && text.EndsWith("__", StringComparison.Ordinal);

    /// <inheritdoc />
    public CodeLexResult Lex(string text) => TreeSitterScanner.Lex(text, language, Grammar, IsKeyword);

    /// <summary>A type-checker, linter, coverage or formatter directive anywhere in a comment.</summary>
    [GeneratedRegex(@"#\s*(?:type:|noqa\b|pragma:|pylint:|mypy:|fmt:\s*(?:off|on)\b)")]
    private static partial Regex ToolDirective();

    /// <summary>The PEP 263 source-encoding declaration.</summary>
    [GeneratedRegex("coding[:=]")]
    private static partial Regex EncodingCookie();
}
