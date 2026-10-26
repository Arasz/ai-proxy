using System.Collections.Frozen;
using System.ComponentModel;
using System.Text.Json;
using AiProxy.Client;
using AiProxy.Mcp;
using Spectre.Console;
using Spectre.Console.Cli;

// ReSharper disable AccessToDisposedClosure

namespace AiProxy.Cli;

/// <summary>Registers and runs the proxy's console commands.</summary>
public sealed class CliApplication(GainRuntime runtime)
{
    private const int OperationCancelledReturnCode = 130;

    private static readonly FrozenSet<string> Commands = new[]
    {
        "-h", "-?", "--help", GainCommand.Command, McpServerProcess.Command, HarnessProcess.Command
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsCommandInvocation(string[] args)
    {
        return args.Length > 0 && Commands.Contains(args[0]);
    }

    public static async Task<int> RunProcessAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();

        ConsoleCancelEventHandler interrupt = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += interrupt;

        var services = new ServiceCollection();
        services.AddProxyHttpClient();
        await using var provider = services.BuildServiceProvider();
        var runtime = new GainRuntime(
            AnsiConsole.Console,
            Console.Out,
            Console.Error,
            provider.GetRequiredService<IProxyClient>(),
            Environment.CurrentDirectory,
            !Console.IsInputRedirected && !Console.IsOutputRedirected,
            TimeProvider.System,
            AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) }));

        try
        {
            return await new CliApplication(runtime).RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= interrupt;
        }
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var separator = args.Length > 0 && args[0] == HarnessProcess.Command ? Array.IndexOf(args, "--") : -1;
        var harnessArguments = separator >= 0 ? args[(separator + 1)..] : null;
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("ai-proxy");
            config.ConfigureConsole(runtime.Console);
            config.PropagateExceptions();
            config.Settings.StrictParsing = true;
            config.AddCommand<GainCommand>(GainCommand.Command)
                .WithData(runtime)
                .WithDescription("Show estimated initial-request token reductions and history.");
            config.AddCommand<McpCommand>(McpServerProcess.Command)
                .WithDescription("Run the retrieval MCP server over stdio.");
            var harness = config.AddCommand<HarnessProcess>(HarnessProcess.Command)
                .WithDescription("Launch a harness executable through the running proxy.")
                .WithExample(HarnessProcess.Command, "claude", "--", "--model", "opus");
            if (harnessArguments is not null)
            {
                harness.WithData(harnessArguments);
            }
        });
        try
        {
            return await app.RunAsync(separator >= 0 ? args[..separator] : args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperationCancelledReturnCode;
        }
        catch (Exception exception) when (exception is CommandAppException or HttpRequestException or JsonException or ArgumentException or IOException or InvalidOperationException or Win32Exception)
        {
            await runtime.Error.WriteLineAsync($"ai-proxy: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}

/// <summary>The console, proxy client, and process context used by the CLI.</summary>
public sealed record GainRuntime(
    IAnsiConsole Console,
    TextWriter Output,
    TextWriter Error,
    IProxyClient Client,
    string Directory,
    bool Interactive,
    TimeProvider Time,
    IAnsiConsole? PromptConsole = null);
