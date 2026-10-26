using AiProxy.Client;
using Spectre.Console;

namespace AiProxy.Cli;

/// <summary>Runs <c>ai-proxy gain</c> on the process console.</summary>
public sealed class CliProcess
{
    /// <summary>The first argument that selects the gain command.</summary>
    public const string Command = "gain";

    /// <summary>Runs the command line, <c>gain</c> included, and returns the process exit code; Ctrl+C cancels.</summary>
    public async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        var runtime = new GainRuntime(AnsiConsole.Console, Console.Out, Console.Error,
            ProxyClient.CreateHttpClient, Environment.CurrentDirectory,
            !Console.IsInputRedirected && !Console.IsOutputRedirected, TimeProvider.System,
            AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) }));
        return await new CliApplication(runtime).RunAsync(args, cancellation.Token).ConfigureAwait(false);
    }
}
