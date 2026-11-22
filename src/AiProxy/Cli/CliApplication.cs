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

    private static readonly CommandRegistration[] Registrations =
    [
        new(GainCommand.Command, static (config, runtime, _) => config.AddCommand<GainCommand>(GainCommand.Command)
            .WithData(runtime)
            .WithDescription("Show estimated initial-request token reductions and history.")),
        new(ReplayCommand.Command, static (config, runtime, _) => config.AddCommand<ReplayCommand>(ReplayCommand.Command)
            .WithData(runtime)
            .WithDescription("Replay stored requests through the compression pipeline and show what it would save.")),
        new(DoctorCommand.Command, static (config, runtime, _) => config.AddCommand<DoctorCommand>(DoctorCommand.Command)
            .WithData(runtime)
            .WithDescription("Check the setup without changing anything.")),
        new(StatusCommand.Command, static (config, runtime, _) => config.AddCommand<StatusCommand>(StatusCommand.Command)
            .WithData(runtime)
            .WithDescription("Show the running proxy instance (exit 3 when none runs).")),
        new(StopCommand.Command, static (config, runtime, _) => config.AddCommand<StopCommand>(StopCommand.Command)
            .WithData(runtime)
            .WithDescription("Gracefully stop the running proxy instance.")),
        new(ShortcutCommand.Command, static (config, runtime, _) => config.AddCommand<ShortcutCommand>(ShortcutCommand.Command)
            .WithData(runtime)
            .WithDescription("Create or remove the aph shortcut for ai-proxy harness.")),
        new(McpServerProcess.Command, static (config, _, _) => config.AddCommand<McpCommand>(McpServerProcess.Command)
            .WithDescription("Run the retrieval MCP server over stdio.")),
        new(HarnessProcess.Command, static (config, _, forwarded) =>
        {
            var harness = config.AddCommand<HarnessProcess>(HarnessProcess.Command)
                .WithDescription("Launch a harness executable through the running proxy.")
                .WithExample(HarnessProcess.Command, "claude", "--model", "opus");
            if (forwarded is not null)
            {
                harness.WithData(forwarded);
            }
        })
    ];

    private static readonly FrozenSet<string> Commands = Registrations.Select(static registration => registration.Name).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The names of the commands this application registers, the same list that registers them.</summary>
    public static IReadOnlyCollection<string> CommandNames => Commands;

    public static bool IsCommandInvocation(string[] args) => IsRootHelp(args) || (args.Length > 0 && (Commands.Contains(args[0]) || args[0] is "--version"));

    private static bool IsRootHelp(string[] args) => args.Any(argument => argument is "-h" or "-?" or "--help") && (args.Length == 0 || !Commands.Contains(args[0]));

    /// <summary>Reports a command line that is neither a command nor valid start options: the parse error, then the usage, both on stderr.</summary>
    public static async Task<int> RunRejectedAsync(string[] args)
    {
        var code = await RunProcessAsync(args).ConfigureAwait(false);
        await Console.Error.WriteLineAsync($"Not understood: {string.Join(' ', args)}\n{EntryPoint.StartUsage}").ConfigureAwait(false);
        return code;
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
        if (args is ["--version"])
        {
            await runtime.Output.WriteLineAsync(EntryPoint.ProductVersion).ConfigureAwait(false);
            return 0;
        }

        if (IsRootHelp(args))
        {
            await runtime.Output.WriteLineAsync(EntryPoint.StartUsage).ConfigureAwait(false);
            args = ["--help"];
        }

        var split = HarnessArguments.Split(args);
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("ai-proxy");
            config.ConfigureConsole(runtime.Console);
            config.PropagateExceptions();
            config.Settings.StrictParsing = true;
            foreach (var registration in Registrations)
            {
                registration.Register(config, runtime, split.Forwarded);
            }
        });
        try
        {
            return await app.RunAsync(split.Launcher, cancellationToken).ConfigureAwait(false);
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

/// <summary>One console command: its name and how it is added to the command application.</summary>
/// <param name="Name">The command name.</param>
/// <param name="Register">Adds the command to the configurator, given the runtime and any forwarded harness arguments.</param>
internal sealed record CommandRegistration(string Name, Action<IConfigurator, GainRuntime, string[]?> Register);

/// <summary>The console, proxy client, and process context used by the CLI.</summary>
public sealed record GainRuntime(
    IAnsiConsole Console,
    TextWriter Output,
    TextWriter Error,
    IProxyClient Client,
    string Directory,
    bool Interactive,
    TimeProvider Time,
    IAnsiConsole? PromptConsole = null,
    Func<string, string?>? Variable = null);