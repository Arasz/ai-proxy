namespace AiProxy;

/// <summary>Recognizes the Codex CLI and builds the arguments the harness launches it with.</summary>
public static class CodexHarness
{
    private const string Name = "codex";
    private const string NoDaemon = "--no-daemon";
    private const string Remote = "--remote";
    private const string RemotePrefix = "--remote=";

    private static readonly string[] DaemonCommands = ["remote-control", "app-server"];

    public static bool IsCodex(string executable)
    {
        return Path.GetFileNameWithoutExtension(executable).Equals(Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The arguments to launch with: Codex gets <c>--no-daemon</c> first unless the arguments already choose a daemon or remote.</summary>
    public static IReadOnlyList<string> LaunchArguments(string executable, IReadOnlyList<string> arguments)
    {
        return IsCodex(executable) && !ChoosesDaemon(arguments) ? [NoDaemon, .. arguments] : arguments;
    }

    private static bool ChoosesDaemon(IReadOnlyList<string> arguments)
    {
        return (arguments.Count > 0 && DaemonCommands.Contains(arguments[0], StringComparer.Ordinal))
               || arguments.Any(static argument => argument is NoDaemon or Remote || argument.StartsWith(RemotePrefix, StringComparison.Ordinal));
    }
}
