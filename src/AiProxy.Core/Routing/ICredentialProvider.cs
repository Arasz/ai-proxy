using AiProxy.Core.Providers;

namespace AiProxy.Core.Routing;

/// <summary>Holds the proxy's own credential for a provider dialect.</summary>
public interface ICredentialProvider
{
    /// <summary>Whether the proxy holds a credential for the dialect, and what it is.</summary>
    bool TryGetCredential(Dialect dialect, out string credential);
}
