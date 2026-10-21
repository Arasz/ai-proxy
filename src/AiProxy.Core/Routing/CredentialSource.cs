namespace AiProxy.Core.Routing;

/// <summary>Whose credential travels with a request to the provider.</summary>
public enum CredentialSource
{
    /// <summary>The caller's own credential, because the caller addressed that provider's endpoint.</summary>
    Caller,

    /// <summary>The proxy's own credential for the dialect, because the caller addressed no provider endpoint.</summary>
    Proxy,
}
