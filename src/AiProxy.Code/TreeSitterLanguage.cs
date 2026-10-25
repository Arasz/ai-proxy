using TreeSitter;

namespace AiProxy.Code;

/// <summary>A Tree-sitter grammar loaded on first use; a grammar or native library that fails to load yields null, once.</summary>
public sealed class TreeSitterLanguage(string name)
{
    private readonly Lazy<Language?> _language = new(() => Load(name));

    /// <summary>The loaded grammar, or null when it could not be loaded.</summary>
    internal Language? Value => _language.Value;

    private static Language? Load(string name)
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
            return null;
        }
    }
}
