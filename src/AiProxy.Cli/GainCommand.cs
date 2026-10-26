using System.Globalization;
using System.Text.Json;
using AiProxy.Client;
using AiProxy.Core.Gain;
using Spectre.Console.Cli;
using Spectre.Console;
namespace AiProxy.Cli;

public sealed class GainCommand : AsyncCommand<GainSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GainSettings settings, CancellationToken cancellationToken)
    {
        var runtime = context.Data as GainRuntime ?? throw new InvalidOperationException("The gain command was registered without its runtime.");
        if (settings.Quota || settings.Tier is not null)
        {
            throw new ArgumentException("Quota/tier estimates are not supported: no verified subscription quota model is available. Use the estimated initial-request reductions instead.");
        }
        if (settings.Shadow)
        {
            throw new ArgumentException("Shadow statistics are not supported: this proxy does not record shadow executions.");
        }
        if (settings.SkipEnv)
        {
            throw new ArgumentException("--skip-env is not supported: gain is a statistics UI and launches no child processes.");
        }
        if (GainSettings.Validator.Error(settings, runtime) is { } validationError)
        {
            throw new ArgumentException(validationError);
        }
        var query = Query(settings, runtime.Directory);
        if (query.From is { } from && query.To is { } to && from >= to)
        {
            throw new ArgumentException("The from timestamp must be before the to timestamp.");
        }
        if (settings.Reset && !settings.Yes)
        {
            if (!runtime.Interactive)
            {
                throw new ArgumentException("Reset needs confirmation. Run interactively or pass --yes after reviewing the filters.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!(runtime.PromptConsole ?? runtime.Console).Confirm("Reset gain and recall statistics matching these filters?", false))
            {
                return 0;
            }
        }
        using var http = runtime.CreateClient(settings.Endpoint);
        var client = new ProxyClient(http);
        if (settings.Reset)
        {
            var reset = await client.ResetGainAsync(query, cancellationToken).ConfigureAwait(false);
            if (settings.Format == GainFormats.Json)
            {
                await runtime.Output.WriteLineAsync(JsonSerializer.Serialize(reset, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ConfigureAwait(false);
            }
            else if (settings.Format == GainFormats.Csv)
            {
                await runtime.Output.WriteLineAsync($"removed,cutoff\r\n{reset.Removed.ToString(CultureInfo.InvariantCulture)},{reset.Cutoff:O}").ConfigureAwait(false);
            }
            else
            {
                runtime.Console.Write(new Text($"Reset {reset.Removed} metadata rows through {reset.Cutoff:O}. Originals were preserved.\n"));
            }
            return 0;
        }
        var report = await client.GetGainAsync(query, cancellationToken).ConfigureAwait(false);
        if (settings.Watch && !settings.Text)
        {
            await new GainWatch(runtime).RunAsync(client, query, settings, report, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        if (settings.Format == GainFormats.Json)
        {
            await runtime.Output.WriteLineAsync(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ConfigureAwait(false);
        }
        else if (settings.Format == GainFormats.Csv)
        {
            await runtime.Output.WriteAsync(new GainCsv().Render(report, settings)).ConfigureAwait(false);
        }
        else
        {
            runtime.Console.Write(new GainView().Render(report, settings));
        }
        return 0;
    }
    private static GainQuery Query(GainSettings settings, string directory) => new(
        settings.ProjectPath ?? (settings.Project ? Path.GetFullPath(directory) : null),
        ParseDate(settings.From), ParseDate(settings.To), settings.Provider, settings.Method,
        settings.Rule, settings.Layer, settings.Pipeline, settings.Limit);

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (value is null) { return null; }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) { return parsed; }
        throw new ArgumentException("Expected an ISO timestamp, for example 2026-10-25T00:00:00Z.");
    }
}
