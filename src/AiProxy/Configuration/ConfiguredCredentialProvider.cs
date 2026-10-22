using AiProxy.Core.Providers;
using AiProxy.Core.Routing;

namespace AiProxy.Configuration;

/// <summary>The proxy's own credentials, which live in the environment and nowhere else.</summary>
public sealed class ConfiguredCredentialProvider : ICredentialProvider
{
    private readonly ProxyOptions _options;

    /// <summary>Holds the configured credentials.</summary>
    public ConfiguredCredentialProvider(ProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <summary>Whether the proxy holds a credential for the dialect, and what it is.</summary>
    public bool TryGetCredential(Dialect dialect, out string credential) =>
        _options.Credentials.TryGetValue(dialect, out credential!);
}
