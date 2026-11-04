using System.ComponentModel;
using AiProxy.Core.Gain;
using AiProxy.Core.Providers;
using FluentValidation;
using JetBrains.Annotations;
using Spectre.Console.Cli;

namespace AiProxy.Cli;

/// <summary>Where the setup check looks: the proxy's API and listener, its data directory, and the host to probe.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class DoctorSettings : CommandSettings
{
    public const string DefaultHost = "api.anthropic.com";

    [CommandOption("--endpoint <URL>")]
    [DefaultValue(GainApi.DefaultEndpoint)]
    [Description("Base URL of the proxy's local API.")]
    public string Endpoint { get; init; } = GainApi.DefaultEndpoint;

    [CommandOption("--proxy <URL>")]
    [Description("HTTP proxy URL (defaults to loopback and Proxy:Port or the default port).")]
    public string? Proxy { get; init; }

    [CommandOption("--data-directory <PATH>")]
    [Description("The proxy's data directory (defaults to ~/.ai-proxy).")]
    public string? DataDirectory { get; init; }

    [CommandOption("--host <HOST>")]
    [DefaultValue(DefaultHost)]
    [Description("Provider host to probe through the proxy.")]
    public string Host { get; init; } = DefaultHost;

    [CommandOption("-f|--format <FORMAT>")]
    [DefaultValue(GainFormats.Text)]
    [Description("Output format: text, json.")]
    public string Format { get; init; } = GainFormats.Text;

    internal sealed class Validator : AbstractValidator<DoctorSettings>
    {
        public Validator()
        {
            ClassLevelCascadeMode = CascadeMode.Stop;

            RuleFor(static settings => settings.Format)
                .Must(static format => format is GainFormats.Text or GainFormats.Json)
                .WithMessage("Output format must be text or json.");
            RuleFor(static settings => settings.Endpoint)
                .Must(static endpoint => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                .WithMessage("The endpoint must be an absolute HTTP or HTTPS URL.");
            RuleFor(static settings => settings.Proxy)
                .Must(static proxy => proxy is null || (Uri.TryCreate(proxy, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp))
                .WithMessage("The proxy must be an absolute HTTP URL.");
            RuleFor(static settings => settings.Host)
                .Must(static host => ProviderHostCatalog.Hosts.Contains(host))
                .WithMessage("The host must be one of the provider hosts the proxy inspects: " + string.Join(", ", ProviderHostCatalog.Hosts) + ".");
        }

        public static string? Error(DoctorSettings settings)
        {
            return new Validator().Validate(settings).Errors.FirstOrDefault()?.ErrorMessage;
        }
    }
}
