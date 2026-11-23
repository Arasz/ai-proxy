using System.Text.Json;
using AiProxy.Client;
using AiProxy.Core.Gain;
using Spectre.Console;

namespace AiProxy.Cli;

/// <summary>Refreshes the same gain view in an alternate terminal screen until cancelled.</summary>
public sealed class GainWatch(GainRuntime runtime)
{
    public Task RunAsync(IProxyClient client, ProxyApi api, GainQuery query, GainSettings settings, GainReport initial, CancellationToken cancellationToken) =>
        Task.Run(() => runtime.Console.AlternateScreen(() =>
            RefreshAsync(client, api, query, settings, initial, cancellationToken).GetAwaiter().GetResult()), cancellationToken);

    private async Task RefreshAsync(IProxyClient client, ProxyApi api, GainQuery query, GainSettings settings, GainReport initial, CancellationToken cancellationToken)
    {
        var view = new GainView();
        var shown = initial;
        await runtime.Console.Live(new Panel(view.Render(initial, settings)).Header("gain (Ctrl+C exits)").Expand())
            .AutoClear(true).StartAsync(async display =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), runtime.Time, cancellationToken).ConfigureAwait(false);
                    var report = await client.GetGainAsync(api, query, cancellationToken).ConfigureAwait(false);
                    if (Changed(shown, report))
                    {
                        display.UpdateTarget(new Panel(view.Render(report, settings)).Header("gain (Ctrl+C exits)").Expand());
                        shown = report;
                    }
                }
            }).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="next"/> shows anything <paramref name="shown"/> does not; the generated time alone is not a change.</summary>
    internal static bool Changed(GainReport shown, GainReport next) =>
        JsonSerializer.Serialize(shown with { GeneratedAt = default }) != JsonSerializer.Serialize(next with { GeneratedAt = default });
}
