using AiProxy.Core.Providers;

namespace AiProxy.Core.Routing;

/// <summary>The upstreams the proxy knows, looked up without any I/O.</summary>
public sealed class UpstreamCatalog
{
    private readonly IReadOnlyDictionary<Dialect, Uri> _providerUpstreams;

    /// <summary>Holds the upstream of each provider dialect that is configured.</summary>
    public UpstreamCatalog(IReadOnlyDictionary<Dialect, Uri> providerUpstreams)
    {
        ArgumentNullException.ThrowIfNull(providerUpstreams);

        _providerUpstreams = providerUpstreams;
    }

    /// <summary>The upstream of a dialect, when one is configured.</summary>
    public bool TryGetProviderUpstream(Dialect dialect, out Uri upstream) =>
        _providerUpstreams.TryGetValue(dialect, out upstream!);
}
