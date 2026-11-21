using AiProxy.Hosting.Proxy;
using AiProxy.Replay;
using Microsoft.Extensions.Configuration;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Runs stored request bodies through today's compression pipeline and prints what it would save; reads the databases and changes nothing.</summary>
public sealed class ReplayCommand : AsyncCommand<ReplaySettings>
{
    public const string Command = "replay";

    public override async Task<int> ExecuteAsync(CommandContext context, ReplaySettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context.Data);
        var runtime = (GainRuntime)context.Data;
        if (ReplaySettings.Validator.Error(settings) is { } validationError)
        {
            throw new ArgumentException(validationError);
        }

        var home = ProxyDataDirectory.FromEnvironment(settings.Home);
        using var runner = ReplayRunner.Create(home, settings.Minimum, new ConfigurationBuilder().AddEnvironmentVariables().Build());
        var run = await runner.RunAsync(settings.Sources, settings.SinceTime, settings.IncludeEncoded, cancellationToken).ConfigureAwait(false);
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
