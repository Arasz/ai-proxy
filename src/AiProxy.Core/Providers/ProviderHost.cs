namespace AiProxy.Core.Providers;

/// <summary>A provider API host as the proxy treats it.</summary>
/// <param name="Dialect">The request dialect of the host.</param>
/// <param name="HarnessOnly">Whether the host also serves browsers, so the proxy decrypts it only for credentialed tunnels and never changes its traffic.</param>
/// <param name="ChatCcr">Whether recoverable compression serves the host's Chat Completions requests.</param>
public sealed record ProviderHost(Dialect Dialect, bool HarnessOnly, bool ChatCcr = false);
