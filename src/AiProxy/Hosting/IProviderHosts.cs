using AiProxy.Core.Providers;

namespace AiProxy.Hosting;

/// <summary>Decides which request hosts are provider APIs the proxy inspects.</summary>
public interface IProviderHosts
{
    /// <summary>Returns the provider dialect for a host, or null to pass the request through untouched.</summary>
    Dialect? Find(string host);

    /// <summary>Every host <see cref="Find" /> recognises; the root certificate's name constraint is derived from them.</summary>
    IReadOnlyCollection<string> Hosts { get; }
}

/// <summary>The canonical provider API hosts from <see cref="ProviderHostCatalog" />.</summary>
public sealed class CanonicalProviderHosts : IProviderHosts
{
    /// <inheritdoc />
    public Dialect? Find(string host) => ProviderHostCatalog.Find(host);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Hosts => ProviderHostCatalog.Hosts;
}
