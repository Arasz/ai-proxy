using AiProxy.Hosting.Proxy;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Gracefully stops the running proxy and exits 0 (stopped or not running) or 1 (in progress, refused, identity mismatch, timeout).</summary>
public sealed class StopCommand : AsyncCommand<StopSettings>
{
    public const string Command = "stop";

    public override async Task<int> ExecuteAsync(CommandContext context, StopSettings settings, CancellationToken cancellationToken)
    {
        var runtime = context.Data as GainRuntime ?? throw new InvalidOperationException("The stop command was registered without its runtime.");
        var directory = ProxyDataDirectory.Resolve(settings.DataDirectory, runtime.Variable ?? Environment.GetEnvironmentVariable);
        var stopped = await new ProxyStop(new ProxyLease()).StopAsync(directory, cancellationToken).ConfigureAwait(false);
        var message = stopped is null
            ? $"ai-proxy is not running (data directory {directory})."
            : $"Stopped ai-proxy (pid {stopped.Pid}).";
        await runtime.Output.WriteLineAsync(message).ConfigureAwait(false);
        return 0;
    }
}
