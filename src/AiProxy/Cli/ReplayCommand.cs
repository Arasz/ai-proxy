using AiProxy.Hosting.Proxy;
using AiProxy.Replay;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Runs stored request bodies through today's compression pipeline and prints what it would save; reads the databases and changes nothing.</summary>
public sealed class ReplayCommand : AsyncCommand<ReplaySettings>
{
    public const string Command = "replay";

    public override async Task<int> ExecuteAsync(CommandContext context, ReplaySettings settings, CancellationToken cancellationToken)
    {
        var runtime = context.Data as GainRuntime ?? throw new InvalidOperationException("The replay command was registered without its runtime.");
        if (ReplaySettings.Validator.Error(settings) is { } validationError)
        {
            throw new ArgumentException(validationError);
        }

        var home = ProxyDataDirectory.FromEnvironment(settings.Home);
        var scope = settings.Source switch
        {
            ReplaySettings.ContentSource => ReplayScope.Content,
            ReplaySettings.CaptureSource => ReplayScope.Capture,
            _ => ReplayScope.Both
        };
        using var runner = ReplayRunner.Create(home, settings.Minimum);
        var run = await runner.RunAsync(scope, settings.SinceTime, cancellationToken).ConfigureAwait(false);
        var view = new ReplayView(home, settings, run);
        if (settings.Json)
        {
            await runtime.Output.WriteLineAsync(view.Json()).ConfigureAwait(false);
        }
        else
        {
            view.Render(runtime.Console);
        }

        return 0;
    }
}
