using System.ComponentModel;
using AiProxy.Client;
using Spectre.Console.Cli;
namespace AiProxy.Cli;

/// <summary>Views and filters for the proxy's gain report.</summary>
public sealed class GainSettings : CommandSettings
{
    [CommandOption("-p|--project"), Description("Filter to the current project directory.")]
    public bool Project { get; init; }
    [CommandOption("-f|--format <FORMAT>"), DefaultValue("text"), Description("Output format: text, json, csv.")]
    public string Format { get; init; } = "text";
    [CommandOption("--endpoint <URL>"), DefaultValue(ProxyClientOptions.DefaultEndpoint)]
    public string Endpoint { get; init; } = ProxyClientOptions.DefaultEndpoint;
    [CommandOption("-g|--graph"), Description("Show an ASCII graph of daily estimated savings.")]
    public bool Graph { get; init; }
    [CommandOption("-H|--history"), Description("Show recent provider request history.")]
    public bool History { get; init; }
    [CommandOption("-d|--daily"), Description("Show daily totals (UTC).")]
    public bool Daily { get; init; }
    [CommandOption("-w|--weekly"), Description("Show Monday-start weekly totals (UTC).")]
    public bool Weekly { get; init; }
    [CommandOption("-m|--monthly"), Description("Show monthly totals (UTC).")]
    public bool Monthly { get; init; }
    [CommandOption("-a|--all"), Description("Show daily, weekly and monthly totals.")]
    public bool All { get; init; }
    [CommandOption("-F|--failures"), Description("Show recorded proxy failures.")]
    public bool Failures { get; init; }
    [CommandOption("--recalls"), Description("Show retrieval cost and errors.")]
    public bool Recalls { get; init; }
    [CommandOption("--by-method")]
    public bool ByMethod { get; init; }
    [CommandOption("--by-rule")]
    public bool ByRule { get; init; }
    [CommandOption("--by-layer")]
    public bool ByLayer { get; init; }
    [CommandOption("--by-pipeline")]
    public bool ByPipeline { get; init; }
    [CommandOption("--project-path <PATH>"), Description("Filter to an explicit project identity.")]
    public string? ProjectPath { get; init; }
    [CommandOption("--from <TIMESTAMP>"), Description("Inclusive ISO timestamp.")]
    public string? From { get; init; }
    [CommandOption("--to <TIMESTAMP>"), Description("Exclusive ISO timestamp.")]
    public string? To { get; init; }
    [CommandOption("--provider <NAME>")]
    public string? Provider { get; init; }
    [CommandOption("--method <NAME>")]
    public string? Method { get; init; }
    [CommandOption("--rule <NAME>")]
    public string? Rule { get; init; }
    [CommandOption("--layer <NAME>")]
    public string? Layer { get; init; }
    [CommandOption("--pipeline <NAME>"), Description("Filter to one pipeline and show its attribution.")]
    public string? Pipeline { get; init; }
    [CommandOption("--limit <COUNT>"), DefaultValue(50), Description("Maximum recent history rows (1-1000).")]
    public int Limit { get; init; } = 50;
    [CommandOption("-q|--quota"), Description("Unavailable: subscription quotas are not measured.")]
    public bool Quota { get; init; }
    [CommandOption("-t|--tier <TIER>"), Description("Unavailable: no verified subscription quota model.")]
    public string? Tier { get; init; }
    [CommandOption("--shadow"), Description("Unavailable: shadow rule executions are not recorded.")]
    public bool Shadow { get; init; }
    [CommandOption("--skip-env"), Description("Unavailable: gain does not launch child processes.")]
    public bool SkipEnv { get; init; }
    [CommandOption("--reset"), Description("Reset matching gain and recall statistics, preserving originals.")]
    public bool Reset { get; init; }
    [CommandOption("--yes"), Description("Confirm a statistics reset without prompting.")]
    public bool Yes { get; init; }
    [CommandOption("--watch"), Description("Live full-screen report; Ctrl+C exits.")]
    public bool Watch { get; init; }
    [CommandOption("--text"), Description("Force a static report, overriding --watch.")]
    public bool Text { get; init; }

    internal static class Validator
    {
        public static string? Error(GainSettings settings, bool interactive)
        {
            if (settings.Format is not ("text" or "json" or "csv")) { return "Output format must be text, json or csv."; }
            if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")) { return "The endpoint must be an absolute HTTP or HTTPS URL."; }
            if (settings.Limit is < 1 or > 1000) { return "History limit must be between 1 and 1000."; }
            if (settings.Project && settings.ProjectPath is not null) { return "Choose --project or --project-path, not both."; }
            if (settings.Yes && !settings.Reset) { return "--yes is only valid with --reset."; }
            if (settings.Reset && settings.Watch && !settings.Text) { return "Reset cannot be combined with --watch. Use --text for a static reset."; }
            if (settings.Watch && !settings.Text && settings.Format != "text") { return "Watch requires text format; use --text for static JSON/CSV."; }
            if (settings.Watch && !settings.Text && !interactive) { return "Watch requires an interactive terminal. Use --text for pipes or redirected output."; }
            return null;
        }
    }
}
