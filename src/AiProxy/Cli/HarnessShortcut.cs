namespace AiProxy.Cli;

/// <summary>The <c>aph</c> launcher beside the <c>ai-proxy</c> found on PATH; it never touches a file it did not create.</summary>
/// <param name="ToolPath">The <c>ai-proxy</c> executable the launcher calls.</param>
/// <param name="Windows">Whether to write a command script instead of a shell script.</param>
internal sealed record HarnessShortcut(string ToolPath, bool Windows)
{
    private const string Marker = "Created by ai-proxy shortcut.";

    /// <summary>Where the launcher lives.</summary>
    public string FilePath => Path.Combine(Path.GetDirectoryName(ToolPath) ?? "", Windows ? "aph.cmd" : "aph");

    private string Content => Windows
        ? $"@echo off\r\nrem {Marker}\r\n\"{ToolPath}\" harness %*\r\n"
        : $"#!/bin/sh\n# {Marker}\nexec '{ToolPath.Replace("'", @"'\''", StringComparison.Ordinal)}' harness \"$@\"\n";

    /// <summary>The shortcut for the first <c>ai-proxy</c> on <paramref name="path"/>, or null when there is none.</summary>
    public static HarnessShortcut? Find(string? path, bool windows)
    {
        var names = windows ? new[] { "ai-proxy.exe", "ai-proxy.cmd" } : ["ai-proxy"];
        var tool = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
        return tool is null ? null : new HarnessShortcut(Path.GetFullPath(tool), windows);
    }

    /// <summary>Writes the launcher, replacing only an earlier one of ours, and says what happened.</summary>
    public string Create()
    {
        RefuseForeignFile();
        File.WriteAllText(FilePath, Content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(FilePath, File.GetUnixFileMode(FilePath) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        return $"Created {FilePath}. Run a harness with: aph claude";
    }

    /// <summary>Deletes the launcher if it is ours and says what happened.</summary>
    public string Remove()
    {
        if (!File.Exists(FilePath))
        {
            return $"There is no shortcut at {FilePath}.";
        }

        RefuseForeignFile();
        File.Delete(FilePath);
        return $"Removed {FilePath}.";
    }

    private void RefuseForeignFile()
    {
        if (File.Exists(FilePath) && !File.ReadAllText(FilePath).Contains(Marker, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{FilePath} already exists and was not created by ai-proxy; it was left unchanged.");
        }
    }
}
