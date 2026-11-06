namespace AiProxy.Cli;

/// <summary>The kind of launcher a platform runs.</summary>
internal enum ShortcutScript
{
    Shell,
    Batch
}

/// <summary>The <c>aph</c> launcher beside the <c>ai-proxy</c> found on PATH; it never touches a file it did not create.</summary>
/// <param name="ToolPath">The <c>ai-proxy</c> executable the launcher calls.</param>
/// <param name="Script">The launcher kind.</param>
internal sealed record HarnessShortcut(string ToolPath, ShortcutScript Script)
{
    private const string Marker = "Created by ai-proxy shortcut.";

    private const UnixFileMode Executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>Where the launcher lives.</summary>
    public string FilePath => Path.Combine(Path.GetDirectoryName(ToolPath) ?? "", Script == ShortcutScript.Batch ? "aph.cmd" : "aph");

    private string MarkerLine => Script == ShortcutScript.Batch ? $"rem {Marker}" : $"# {Marker}";

    // A batch file is read in the OEM code page, so it names the tool relative to itself and stays ASCII.
    private string Content => Script == ShortcutScript.Batch
        ? $"@echo off\r\n{MarkerLine}\r\n\"%~dp0{Path.GetFileName(ToolPath)}\" harness %*\r\n"
        : $"#!/bin/sh\n{MarkerLine}\nexec '{ToolPath.Replace("'", @"'\''", StringComparison.Ordinal)}' harness \"$@\"\n";

    /// <summary>The shortcut for the first runnable <c>ai-proxy</c> on <paramref name="path"/>, or null when there is none.</summary>
    public static HarnessShortcut? Find(string? path, ShortcutScript script)
    {
        var names = script == ShortcutScript.Batch ? new[] { "ai-proxy.exe", "ai-proxy.cmd" } : ["ai-proxy"];
        var tool = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(IsRunnable);
        return tool is null ? null : new HarnessShortcut(Path.GetFullPath(tool), script);
    }

    /// <summary>Writes the launcher, replacing only an earlier one of ours, and says what happened.</summary>
    public string Create()
    {
        RefuseForeignFile();
        File.WriteAllText(FilePath, Content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(FilePath, File.GetUnixFileMode(FilePath) | Executable);
        }

        return $"Created {FilePath}. Run a harness with: aph claude";
    }

    /// <summary>Deletes the launcher if it is ours and says what happened.</summary>
    public string Remove()
    {
        RefuseForeignFile();
        if (!File.Exists(FilePath))
        {
            return $"There is no shortcut at {FilePath}.";
        }

        File.Delete(FilePath);
        return $"Removed {FilePath}.";
    }

    private static bool IsRunnable(string path)
    {
        return File.Exists(path) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & Executable) != 0);
    }

    private void RefuseForeignFile()
    {
        var file = new FileInfo(FilePath);
        if (file.LinkTarget is not null || (file.Exists && File.ReadLines(FilePath).Skip(1).FirstOrDefault() != MarkerLine))
        {
            throw new ArgumentException($"{FilePath} already exists and was not created by ai-proxy; it was left unchanged.");
        }
    }
}
