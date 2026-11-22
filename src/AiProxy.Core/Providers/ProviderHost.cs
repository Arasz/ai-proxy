namespace AiProxy.Core.Providers;

/// <summary>A provider API host as the proxy treats it.</summary>
/// <param name="Dialect">The request dialect of the host.</param>
/// <param name="HarnessOnly">Whether the host also serves browsers, so the proxy decrypts it only for credentialed tunnels and never changes its traffic.</param>
/// <param name="ChatCcr">Whether recoverable compression serves the host's Chat Completions requests.</param>
public sealed record ProviderHost(Dialect Dialect, bool HarnessOnly, bool ChatCcr = false);

/// <summary>A host that is not a provider API: the proxy passes its traffic through untouched.</summary>
public sealed record NoneProviderHost
{
    /// <summary>The single value.</summary>
    public static NoneProviderHost Instance { get; } = new();
}

/// <summary>What a host lookup finds: a <see cref="ProviderHost" /> to inspect, or <see cref="NoneProviderHost" /> to pass through.</summary>
public union ProviderHostMatch(ProviderHost, NoneProviderHost);
