using AiProxy.Core.Providers;

namespace AiProxy.Hosting;

/// <summary>Decides which request hosts are provider APIs the proxy inspects.</summary>
public interface IProviderHosts
{
    /// <summary>Returns the host's dialect and harness-only flag, or null to pass the request through untouched.</summary>
    ProviderHost? Find(string host);

    /// <summary>Every host <see cref="Find" /> recognises; the root certificate's name constraint is derived from them.</summary>
    IReadOnlyCollection<string> Hosts { get; }
}

/// <summary>The canonical provider API hosts from <see cref="ProviderHostCatalog" />.</summary>
public sealed class CanonicalProviderHosts : IProviderHosts
{
    /// <inheritdoc />
    public ProviderHost? Find(string host) => ProviderHostCatalog.Lookup(host);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Hosts => ProviderHostCatalog.Hosts;
}
