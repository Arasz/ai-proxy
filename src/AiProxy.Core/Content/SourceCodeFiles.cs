namespace AiProxy.Core.Content;

/// <summary>Tells a source file and its language by the extension of its path, and by nothing else.</summary>
public static class SourceCodeFiles
{
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".py"] = "python",
        [".js"] = "javascript",
        [".mjs"] = "javascript",
        [".cjs"] = "javascript",
        [".ts"] = "typescript",
        [".tsx"] = "tsx",
    };

    /// <summary>The language the path's extension names, or null when it names none.</summary>
    /// <param name="path">The path of the file, or null when it is not known.</param>
    public static string? LanguageOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? Languages.GetValueOrDefault(name[dot..]) : null;
    }

    /// <summary>Whether the path names a file whose extension is a known source language.</summary>
    /// <param name="path">The path of the file, or null when it is not known.</param>
    public static bool IsSourceCode(string? path) => LanguageOf(path) is not null;
}
