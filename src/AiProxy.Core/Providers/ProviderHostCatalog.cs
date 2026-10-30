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

    /// <summary>Every canonical provider host.</summary>
    public static IReadOnlyCollection<string> Hosts { get; } = [.. Providers.Keys];

    /// <summary>Returns the provider dialect for a canonical host, or null for default pass-through.</summary>
    public static Dialect? Find(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return Providers.TryGetValue(host.TrimEnd('.'), out var dialect) ? dialect : null;
    }
}
