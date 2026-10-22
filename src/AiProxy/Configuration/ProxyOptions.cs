using AiProxy.Core.Providers;
using AiProxy.Core.Routing;

namespace AiProxy.Configuration;

/// <summary>What the operator configured, read from the environment once at startup.</summary>
/// <param name="Upstreams">The upstream of each provider dialect that is configured.</param>
/// <param name="Credentials">The proxy's own credential for each dialect that has one.</param>
/// <param name="PassThrough">The operator's path-prefix rules.</param>
public sealed record ProxyOptions(
    IReadOnlyDictionary<Dialect, Uri> Upstreams,
    IReadOnlyDictionary<Dialect, string> Credentials,
    IReadOnlyList<PassThroughUpstream> PassThrough)
{
    /// <summary>The upstream variable of each dialect.</summary>
    private static readonly (Dialect Dialect, string UpstreamVariable, string CredentialVariable)[] Variables =
    [
        (Dialect.Anthropic, "PROXY_ANTHROPIC_UPSTREAM", "PROXY_ANTHROPIC_KEY"),
        (Dialect.OpenAi, "PROXY_OPENAI_UPSTREAM", "PROXY_OPENAI_KEY"),
        (Dialect.Gemini, "PROXY_GEMINI_UPSTREAM", "PROXY_GEMINI_KEY"),
    ];

    /// <summary>Reads the configuration, and leaves out whatever the operator did not set.</summary>
    public static ProxyOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var upstreams = new Dictionary<Dialect, Uri>();
        var credentials = new Dictionary<Dialect, string>();

        foreach (var (dialect, upstreamVariable, credentialVariable) in Variables)
        {
            var upstream = configuration[upstreamVariable];
            if (!string.IsNullOrWhiteSpace(upstream))
            {
                upstreams[dialect] = new Uri(upstream.Trim(), UriKind.Absolute);
            }

            var credential = configuration[credentialVariable];
            if (!string.IsNullOrWhiteSpace(credential))
            {
                credentials[dialect] = credential.Trim();
            }
        }

        return new ProxyOptions(upstreams, credentials, ParsePassThrough(configuration["PROXY_PASSTHROUGH"]));
    }

    private static IReadOnlyList<PassThroughUpstream> ParsePassThrough(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        var rules = new List<PassThroughUpstream>();

        foreach (var entry in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == entry.Length - 1)
            {
                throw new InvalidOperationException(
                    $"PROXY_PASSTHROUGH entry '{entry}' is not a path prefix and an upstream, separated by '='.");
            }

            var prefix = entry[..separator].Trim();
            var upstream = entry[(separator + 1)..].Trim();

            if (!prefix.StartsWith('/'))
            {
                throw new InvalidOperationException(
                    $"PROXY_PASSTHROUGH entry '{entry}' must start its path prefix with '/'.");
            }

            rules.Add(new PassThroughUpstream(prefix, new Uri(upstream, UriKind.Absolute)));
        }

        return rules;
    }
}
