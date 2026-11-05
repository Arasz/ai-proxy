using AiProxy.Hosting.Proxy;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Describes the running proxy from its control pipe and exits 0 (running), 3 (not running) or 1 (silent or incompatible owner); changes nothing.</summary>
public sealed class StatusCommand : AsyncCommand<StatusSettings>
{
    public const string Command = "status";

    private const int NotRunningExitCode = 3;

    public override async Task<int> ExecuteAsync(CommandContext context, StatusSettings settings, CancellationToken cancellationToken)
    {
        var runtime = context.Data as GainRuntime ?? throw new InvalidOperationException("The status command was registered without its runtime.");
        if (StatusSettings.Validator.Error(settings) is { } validationError)
        {
            throw new ArgumentException(validationError);
        }

        var directory = ProxyDataDirectory.FromEnvironment(settings.DataDirectory);
        var reading = await ProxyControl.ReadOwnerAsync(directory, cancellationToken).ConfigureAwait(false);
        var json = settings.Format == GainFormats.Json;
        switch (reading)
        {
            case { State: ProxyControl.OwnerState.Running, Owner: { } owner }:
                var view = StatusView.From(owner, directory);
                await runtime.Output.WriteLineAsync(json ? view.Json() : view.Text(runtime.Time.GetUtcNow())).ConfigureAwait(false);
                return 0;
            case { State: ProxyControl.OwnerState.NotRunning }:
                await runtime.Output.WriteLineAsync(json ? """{"running":false}""" : $"ai-proxy is not running: no ai-proxy answered on the control pipe for {directory}.").ConfigureAwait(false);
                return NotRunningExitCode;
            case { State: ProxyControl.OwnerState.Incompatible }:
                throw new IOException($"The control peer for {directory} is not a compatible ai-proxy owner.");
            default:
                throw new IOException($"The ai-proxy control peer for {directory} did not answer within the deadline.");
        }
    }
}
