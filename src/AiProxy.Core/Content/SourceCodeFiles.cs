namespace AiProxy.Core.Content;

/// <summary>Tells a source file by the extension of its path, and by nothing else.</summary>
public static class SourceCodeFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".mjs", ".cjs", ".ts", ".tsx",
    };

    /// <summary>Whether the path names a file whose extension is a known source language.</summary>
    /// <param name="path">The path of the file, or null when it is not known.</param>
    public static bool IsSourceCode(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 && Extensions.Contains(name[dot..]);
    }
}
