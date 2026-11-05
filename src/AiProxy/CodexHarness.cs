namespace AiProxy;

/// <summary>Recognizes the Codex CLI and builds the arguments the harness launches it with.</summary>
public static class CodexHarness
{
    private const string Name = "codex";
    private const string NoDaemon = "--no-daemon";
    private const string Remote = "--remote";
    private const string RemotePrefix = "--remote=";

    private static readonly string[] ModelCommands = ["exec", "e", "review", "resume", "fork"];

    private static readonly string[] OtherCommands =
    [
        "agents", "login", "logout", "mcp", "plugin", "app-server", "remote-control", "app", "completion", "update", "doctor", "sandbox", "debug",
        "apply", "a", "queue", "archive", "delete", "migrate-rollouts", "migrate-nfs-db", "unarchive", "cloud", "exec-server", "features", "help"
    ];

    private static readonly string[] OptionsWithValue =
    [
        "-c", "--config", "--enable", "--disable", "--remote", "--remote-auth-token-env", "-i", "--image", "-m", "--model", "--local-provider",
        "-p", "--profile", "-s", "--sandbox", "-C", "--cd", "--add-dir", "-a", "--ask-for-approval"
    ];

    public static bool IsCodex(string executable)
    {
        return Path.GetFileNameWithoutExtension(executable).Equals(Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The arguments to launch with: Codex gets <c>--no-daemon</c> first when it runs the TUI or a model command, unless the leading options already choose a daemon or remote.</summary>
    public static IReadOnlyList<string> LaunchArguments(string executable, IReadOnlyList<string> arguments)
    {
        return IsCodex(executable) && NeedsNoDaemon(arguments) ? [NoDaemon, .. arguments] : arguments;
    }

    private static bool NeedsNoDaemon(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is NoDaemon or Remote || argument.StartsWith(RemotePrefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (OptionsWithValue.Contains(argument, StringComparer.Ordinal))
            {
                index++;
            }
            else if (!argument.StartsWith('-'))
            {
                return !OtherCommands.Contains(argument, StringComparer.Ordinal);
            }
        }

        return true;
    }
}
