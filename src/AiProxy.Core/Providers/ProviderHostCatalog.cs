namespace AiProxy.Core.Providers;

/// <summary>Maps documented provider API hosts, regional ones included, to the request dialect used by compression.</summary>
/// <remarks>Matching is exact: a wildcard would also catch non-API hosts such as <c>mcp.openrouter.ai</c>.</remarks>
public static class ProviderHostCatalog
{
    private static readonly IReadOnlyDictionary<string, Dialect> Providers = new Dictionary<string, Dialect>(StringComparer.OrdinalIgnoreCase)
    {
        ["api.openai.com"] = Dialect.OpenAi,
        ["us.api.openai.com"] = Dialect.OpenAi,
        ["eu.api.openai.com"] = Dialect.OpenAi,
        ["gb.api.openai.com"] = Dialect.OpenAi,
        ["ca.api.openai.com"] = Dialect.OpenAi,
        ["au.api.openai.com"] = Dialect.OpenAi,
        ["jp.api.openai.com"] = Dialect.OpenAi,
        ["kr.api.openai.com"] = Dialect.OpenAi,
        ["sg.api.openai.com"] = Dialect.OpenAi,
        ["in.api.openai.com"] = Dialect.OpenAi,
        ["ae.api.openai.com"] = Dialect.OpenAi,
        ["api.anthropic.com"] = Dialect.Anthropic,
        ["generativelanguage.googleapis.com"] = Dialect.Gemini,
        ["openrouter.ai"] = Dialect.OpenAi,
        ["eu.openrouter.ai"] = Dialect.OpenAi
    };

    /// <summary>Returns the provider dialect for a canonical host, or null for default pass-through.</summary>
    public static Dialect? Find(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return Providers.TryGetValue(host.TrimEnd('.'), out var dialect) ? dialect : null;
    }

    /// <summary>Whether HTTPS for a host should be terminated for provider inspection.</summary>
    public static bool ShouldInspectHttps(string host)
    {
        return !string.IsNullOrWhiteSpace(host) && Find(host) is not null;
    }

    /// <summary>Tests a CONNECT authority such as <c>api.openai.com:443</c>.</summary>
    public static bool ShouldInspectHttpsAuthority(string authority)
    {
        return !string.IsNullOrWhiteSpace(authority) &&
               Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var address) &&
               ShouldInspectHttps(address.Host);
    }
}