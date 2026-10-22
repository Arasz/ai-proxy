using AiProxy.Core.Providers;
using AiProxy.Core.Routing;

namespace AiProxy.Configuration;

/// <summary>What the operator configured, read from the environment once at startup.</summary>
/// <param name="Upstreams">The upstream of each provider dialect that is configured.</param>
/// <param name="PassThrough">The operator's path-prefix rules.</param>
public sealed record ProxyOptions(
    IReadOnlyDictionary<Dialect, Uri> Upstreams,
    IReadOnlyList<PassThroughUpstream> PassThrough)
{
    /// <summary>The upstream variable of each dialect.</summary>
    private static readonly (Dialect Dialect, string UpstreamVariable)[] Variables =
    [
        (Dialect.Anthropic, "PROXY_ANTHROPIC_UPSTREAM"),
        (Dialect.OpenAi, "PROXY_OPENAI_UPSTREAM"),
        (Dialect.Gemini, "PROXY_GEMINI_UPSTREAM"),
    ];

    /// <summary>Reads the configuration, and leaves out whatever the operator did not set.</summary>
    public static ProxyOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var upstreams = new Dictionary<Dialect, Uri>();
        foreach (var (dialect, upstreamVariable) in Variables)
        {
            var upstream = configuration[upstreamVariable];
            if (!string.IsNullOrWhiteSpace(upstream))
            {
                upstreams[dialect] = new Uri(upstream.Trim(), UriKind.Absolute);
            }
        }

        return new ProxyOptions(upstreams, ParsePassThrough(configuration["PROXY_PASSTHROUGH"]));
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
