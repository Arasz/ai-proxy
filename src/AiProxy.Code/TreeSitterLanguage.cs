using Microsoft.Extensions.Logging;
using TreeSitter;

namespace AiProxy.Code;

/// <summary>A Tree-sitter grammar loaded on first use; a grammar or native library that fails to load yields null, once.</summary>
public sealed partial class TreeSitterLanguage(string name, ILogger<TreeSitterLanguage> logger)
{
    public const string PythonKey = "Python";
    public const string JavaScriptKey = "JavaScript";
    public const string TypeScriptKey = "TypeScript";
    public const string TsxKey = "Tsx";

    private readonly Lazy<Language?> _language = new(() => Load(name, logger));

    /// <summary>The loaded grammar, or null when it could not be loaded.</summary>
    internal Language? Value => _language.Value;

    private static Language? Load(string name, ILogger<TreeSitterLanguage> logger)
    {
        Language? language = null;
        try
        {
            language = new Language(name);
            using var parser = new Parser(language);
            return language;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
                                              or BadImageFormatException or InvalidOperationException)
        {
            language?.Dispose();
            Log.GrammarLoadFailed(logger, exception, name);
            return null;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Tree-sitter grammar {Grammar} could not be loaded; code view will use the safe fallback.")]
        public static partial void GrammarLoadFailed(ILogger logger, Exception exception, string grammar);
    }
}