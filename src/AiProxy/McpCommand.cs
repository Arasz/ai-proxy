using System.ComponentModel;
using AiProxy.Core.Gain;
using AiProxy.Hosting.Proxy;
using AiProxy.Mcp;
using JetBrains.Annotations;
using Spectre.Console.Cli;

namespace AiProxy;

[UsedImplicitly]
public sealed class McpCommand : AsyncCommand<McpCommand.Settings>
{
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        return McpServerProcess.RunAsync(settings.Endpoint, ProxyDataDirectory.FromEnvironment(settings.DataDirectory), cancellationToken);
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--endpoint <URL>")]
        [DefaultValue(GainApi.DefaultEndpoint)]
        [Description("Proxy endpoint for retrieval calls.")]
        public string Endpoint { get; init; } = GainApi.DefaultEndpoint;

        [CommandOption("--data-directory <PATH>")]
        [Description("The proxy's data directory, where the API token is (defaults to Proxy__DataDirectory, then ~/.ai-proxy).")]
        public string? DataDirectory { get; init; }
    }
}