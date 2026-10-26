namespace AiProxy.Configuration;

/// <summary>What the operator configured about recoverable compression, bound from <c>Proxy:Ccr</c> (environment form <c>PROXY__CCR__ENABLED</c>).</summary>
public sealed record CcrSettings
{
    /// <summary>Whether large tool results become previews the model can read back with <c>proxy_retrieve</c>; needs compression on.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Also previews for providers the fallback covers; bound but without effect until another provider has its own loop.</summary>
    public bool UnknownProviders { get; init; }
}