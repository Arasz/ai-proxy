using Spectre.Console;
using Spectre.Console.Cli;
namespace AiProxy.Cli;

/// <summary>The terminal, transport factory and process context used by the CLI.</summary>
public sealed record GainRuntime(IAnsiConsole Console, TextWriter Output, TextWriter Error,
    Func<string, HttpClient> CreateClient, string Directory, bool Interactive, TimeProvider Time, IAnsiConsole? PromptConsole = null);

/// <summary>Parses the gain command and routes it to the proxy client.</summary>
public sealed class CliApplication(GainRuntime runtime)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("ai-proxy");
            config.ConfigureConsole(runtime.Console);
            config.PropagateExceptions();
            config.Settings.StrictParsing = true;
            config.AddCommand<GainCommand>("gain").WithData(runtime)
                .WithDescription("Show estimated initial-request token reductions and history.");
        });
        try
        {
            return await app.RunAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception) when (exception is CommandAppException or HttpRequestException or System.Text.Json.JsonException or ArgumentException)
        {
            await runtime.Error.WriteLineAsync("ai-proxy: " + exception.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
