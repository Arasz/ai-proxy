namespace AiProxy.Core.Providers;

/// <summary>Which signal named the provider.</summary>
public enum DialectSource
{
    /// <summary>No signal named a provider, or the signals disagreed.</summary>
    None,

    /// <summary>The request path named the provider's endpoint.</summary>
    Path,

    /// <summary>The request body shape named the provider.</summary>
    Body,

    /// <summary>A request header named the provider.</summary>
    Header,
}
