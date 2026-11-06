using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Creates or removes the <c>aph</c> launcher next to <c>ai-proxy</c>, and exits 1 when it cannot.</summary>
public sealed class ShortcutCommand : AsyncCommand<ShortcutSettings>
{
    public const string Command = "shortcut";

    public override async Task<int> ExecuteAsync(CommandContext context, ShortcutSettings settings, CancellationToken cancellationToken)
    {
        var runtime = context.Data as GainRuntime ?? throw new InvalidOperationException("The shortcut command was registered without its runtime.");
        var path = (runtime.Variable ?? Environment.GetEnvironmentVariable)("PATH");
        try
        {
            var shortcut = HarnessShortcut.Find(path, OperatingSystem.IsWindows() ? ShortcutScript.Batch : ShortcutScript.Shell) ?? throw new ArgumentException("ai-proxy was not found on PATH, so there is no directory to put the shortcut in.");
            var message = settings.Remove ? shortcut.Remove() : shortcut.Create();
            await runtime.Output.WriteLineAsync(message).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"The shortcut could not be changed: {exception.Message}", exception);
        }
    }
}
