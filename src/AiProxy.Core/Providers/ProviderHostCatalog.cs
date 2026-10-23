namespace AiProxy.Core.Providers;

/// <summary>Maps canonical provider API hosts to the request dialect used by compression.</summary>
public static class ProviderHostCatalog
{
    private static readonly IReadOnlyDictionary<string, Dialect> Providers = new Dictionary<string, Dialect>(StringComparer.OrdinalIgnoreCase)
    {
        ["api.openai.com"] = Dialect.OpenAi,
        ["api.anthropic.com"] = Dialect.Anthropic,
        ["generativelanguage.googleapis.com"] = Dialect.Gemini,
        ["openrouter.ai"] = Dialect.OpenAi,
    };

    /// <summary>Returns the provider dialect for a canonical host, or null for default pass-through.</summary>
    public static Dialect? Find(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return Providers.TryGetValue(host.TrimEnd('.'), out var dialect) ? dialect : null;
    }

    /// <summary>Whether HTTPS for a host should be terminated for provider inspection.</summary>
    public static bool ShouldInspectHttps(string host) =>
        !string.IsNullOrWhiteSpace(host) && Find(host) is not null;

    /// <summary>Tests a CONNECT authority such as <c>api.openai.com:443</c>.</summary>
    public static bool ShouldInspectHttpsAuthority(string authority) =>
        !string.IsNullOrWhiteSpace(authority) &&
        Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var address) &&
        ShouldInspectHttps(address.Host);
}
