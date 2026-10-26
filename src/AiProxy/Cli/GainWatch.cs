using AiProxy.Client;
using AiProxy.Core.Gain;
using Spectre.Console;

namespace AiProxy.Cli;

/// <summary>Refreshes the same gain view in an alternate terminal screen until cancelled.</summary>
public sealed class GainWatch(GainRuntime runtime)
{
    public Task RunAsync(IProxyClient client, GainQuery query, GainSettings settings, GainReport initial, CancellationToken cancellationToken)
    {
        return Task.Run(() => runtime.Console.AlternateScreen(() =>
            RefreshAsync(client, query, settings, initial, cancellationToken).GetAwaiter().GetResult()), cancellationToken);
    }

    private async Task RefreshAsync(IProxyClient client, GainQuery query, GainSettings settings, GainReport initial, CancellationToken cancellationToken)
    {
        var view = new GainView();
        await runtime.Console.Live(new Panel(view.Render(initial, settings)).Header("gain (Ctrl+C exits)").Expand())
            .AutoClear(true).StartAsync(async display =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), runtime.Time, cancellationToken).ConfigureAwait(false);
                    var report = await client.GetGainAsync(settings.Endpoint, query, cancellationToken).ConfigureAwait(false);
                    display.UpdateTarget(new Panel(view.Render(report, settings)).Header("gain (Ctrl+C exits)").Expand());
                }
            }).ConfigureAwait(false);
    }
}