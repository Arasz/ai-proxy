using System.ComponentModel;
using AiProxy.Core.Gain;
using FluentValidation;
using JetBrains.Annotations;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Views and filters for the proxy's gain report.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class GainSettings : CommandSettings
{
    [CommandOption("-p|--project")]
    [Description("Filter to the current project directory.")]
    public bool Project { get; init; }

    [CommandOption("-f|--format <FORMAT>")]
    [DefaultValue(GainFormats.Text)]
    [Description("Output format: text, json, csv.")]
    public string Format { get; init; } = GainFormats.Text;

    [CommandOption("--endpoint <URL>")]
    [DefaultValue(GainApi.DefaultEndpoint)]
    [Description("Base URL of the running proxy.")]
    public string Endpoint { get; init; } = GainApi.DefaultEndpoint;

    [CommandOption("-g|--graph")]
    [Description("Show an ASCII graph of daily estimated savings.")]
    public bool Graph { get; init; }

    [CommandOption("-H|--history")]
    [Description("Show recent provider request history.")]
    public bool History { get; init; }

    [CommandOption("-d|--daily")]
    [Description("Show daily totals (UTC).")]
    public bool Daily { get; init; }

    [CommandOption("-w|--weekly")]
    [Description("Show Monday-start weekly totals (UTC).")]
    public bool Weekly { get; init; }

    [CommandOption("-m|--monthly")]
    [Description("Show monthly totals (UTC).")]
    public bool Monthly { get; init; }

    [CommandOption("-a|--all")]
    [Description("Show daily, weekly and monthly totals.")]
    public bool All { get; init; }

    [CommandOption("-F|--failures")]
    [Description("Show recorded proxy failures.")]
    public bool Failures { get; init; }

    [CommandOption("--recalls")]
    [Description("Show retrieval cost and errors.")]
    public bool Recalls { get; init; }

    [CommandOption("--by-method")]
    [Description("Show totals by HTTP method.")]
    public bool ByMethod { get; init; }

    [CommandOption("--by-rule")]
    [Description("Show totals by compression rule.")]
    public bool ByRule { get; init; }

    [CommandOption("--by-layer")]
    [Description("Show totals by request layer.")]
    public bool ByLayer { get; init; }

    [CommandOption("--by-pipeline")]
    [Description("Show totals by pipeline.")]
    public bool ByPipeline { get; init; }

    [CommandOption("--project-path <PATH>")]
    [Description("Filter to an explicit project identity.")]
    public string? ProjectPath { get; init; }

    [CommandOption("--from <TIMESTAMP>")]
    [Description("Inclusive ISO timestamp.")]
    public string? From { get; init; }

    [CommandOption("--to <TIMESTAMP>")]
    [Description("Exclusive ISO timestamp.")]
    public string? To { get; init; }

    [CommandOption("--provider <NAME>")]
    [Description("Filter to one provider, for example anthropic.")]
    public string? Provider { get; init; }

    [CommandOption("--method <NAME>")]
    [Description("Filter to one HTTP method.")]
    public string? Method { get; init; }

    [CommandOption("--rule <NAME>")]
    [Description("Filter to one compression rule.")]
    public string? Rule { get; init; }

    [CommandOption("--layer <NAME>")]
    [Description("Filter to one request layer.")]
    public string? Layer { get; init; }

    [CommandOption("--pipeline <NAME>")]
    [Description("Filter to one pipeline and show its attribution.")]
    public string? Pipeline { get; init; }

    [CommandOption("--limit <COUNT>")]
    [DefaultValue(50)]
    [Description("Maximum recent history rows (1-1000).")]
    public int Limit { get; init; } = 50;

    [CommandOption("-q|--quota", IsHidden = true)]
    [Description("Unavailable: subscription quotas are not measured.")]
    public bool Quota { get; init; }

    [CommandOption("-t|--tier <TIER>", IsHidden = true)]
    [Description("Unavailable: no verified subscription quota model.")]
    public string? Tier { get; init; }

    [CommandOption("--shadow", IsHidden = true)]
    [Description("Unavailable: shadow rule executions are not recorded.")]
    public bool Shadow { get; init; }

    [CommandOption("--skip-env", IsHidden = true)]
    [Description("Unavailable: gain does not launch child processes.")]
    public bool SkipEnv { get; init; }

    [CommandOption("--reset")]
    [Description("Reset matching gain and recall statistics, preserving originals.")]
    public bool Reset { get; init; }

    [CommandOption("--yes")]
    [Description("Confirm a statistics reset without prompting.")]
    public bool Yes { get; init; }

    [CommandOption("--watch")]
    [Description("Live full-screen report; Ctrl+C exits.")]
    public bool Watch { get; init; }

    [CommandOption("--text")]
    [Description("Force a static report, overriding --watch.")]
    public bool Text { get; init; }

    internal sealed class Validator : AbstractValidator<GainSettings>
    {
        private const string InteractiveContextKey = "GainRuntime.Interactive";

        public Validator()
        {
            ClassLevelCascadeMode = CascadeMode.Stop;

            RuleFor(static settings => settings.Format)
                .Must(static format => format is GainFormats.Text or GainFormats.Json or GainFormats.Csv)
                .WithMessage("Output format must be text, json or csv.");
            RuleFor(static settings => settings.Endpoint)
                .Must(static endpoint => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                .WithMessage("The endpoint must be an absolute HTTP or HTTPS URL.");
            RuleFor(static settings => settings.Limit)
                .InclusiveBetween(1, 1000)
                .WithMessage("History limit must be between 1 and 1000.");
            RuleFor(static settings => settings)
                .Must(static settings => !settings.Project || settings.ProjectPath is null)
                .WithMessage("Choose --project or --project-path, not both.");
            RuleFor(static settings => settings)
                .Must(static settings => !settings.Yes || settings.Reset)
                .WithMessage("--yes is only valid with --reset.");
            RuleFor(static settings => settings)
                .Must(static settings => !settings.Reset || !settings.Watch || settings.Text)
                .WithMessage("Reset cannot be combined with --watch. Use --text for a static reset.");
            RuleFor(static settings => settings)
                .Must(static settings => !settings.Watch || settings.Text || settings.Format == GainFormats.Text)
                .WithMessage("Watch requires text format; use --text for static JSON/CSV.");
            RuleFor(static settings => settings)
                .Must(static (_, settings, context) =>
                    !settings.Watch || settings.Text || (bool)context.RootContextData[InteractiveContextKey])
                .WithMessage("Watch requires an interactive terminal. Use --text for pipes or redirected output.");
        }

        public static string? Error(GainSettings settings, GainRuntime runtime)
        {
            var context = new ValidationContext<GainSettings>(settings);
            context.RootContextData[InteractiveContextKey] = runtime.Interactive;
            return new Validator().Validate(context).Errors.FirstOrDefault()?.ErrorMessage;
        }
    }
}