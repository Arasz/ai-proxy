using System.ComponentModel;
using FluentValidation;
using JetBrains.Annotations;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Which instance to describe and how to print it.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class StatusSettings : CommandSettings
{
    [CommandOption("--data-directory <PATH>")]
    [Description("The proxy's data directory (defaults to Proxy__DataDirectory, then ~/.ai-proxy).")]
    public string? DataDirectory { get; init; }

    [CommandOption("-f|--format <FORMAT>")]
    [DefaultValue(GainFormats.Text)]
    [Description("Output format: text, json.")]
    public string Format { get; init; } = GainFormats.Text;

    internal sealed class Validator : AbstractValidator<StatusSettings>
    {
        public Validator()
        {
            RuleFor(static settings => settings.Format)
                .Must(static format => format is GainFormats.Text or GainFormats.Json)
                .WithMessage("Output format must be text or json.");
        }

        public static string? Error(StatusSettings settings)
        {
            return new Validator().Validate(settings).Errors.FirstOrDefault()?.ErrorMessage;
        }
    }
}
