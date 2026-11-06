namespace AiProxy.Core.Providers;

/// <summary>Maps documented provider API hosts, regional ones included, to the request dialect used by compression.</summary>
/// <remarks>Matching is exact: a wildcard would also catch non-API hosts such as <c>mcp.openrouter.ai</c>.</remarks>
public static class ProviderHostCatalog
{
    private static readonly IReadOnlyDictionary<string, ProviderHost> Providers = new Dictionary<string, ProviderHost>(StringComparer.OrdinalIgnoreCase)
    {
        ["api.openai.com"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["us.api.openai.com"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["eu.api.openai.com"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["gb.api.openai.com"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["ca.api.openai.com"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["au.api.openai.com"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["jp.api.openai.com"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["kr.api.openai.com"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["sg.api.openai.com"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["in.api.openai.com"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["ae.api.openai.com"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["api.anthropic.com"] = new(Dialect.Anthropic, HarnessOnly: false),
        ["generativelanguage.googleapis.com"] = new(Dialect.Gemini, HarnessOnly: false),
        ["openrouter.ai"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["eu.openrouter.ai"] = new(Dialect.OpenAi, HarnessOnly: false),
        ["chatgpt.com"] = new(Dialect.OpenAi, HarnessOnly: true)
    };

    /// <summary>Every canonical provider host.</summary>
    public static IReadOnlyCollection<string> Hosts { get; } = [.. Providers.Keys];

    /// <summary>Returns the canonical host's dialect and harness-only flag, or null for default pass-through.</summary>
    public static ProviderHost? Lookup(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return Providers.GetValueOrDefault(host.TrimEnd('.'));
    }

    /// <summary>Returns the provider dialect for a canonical host, or null for default pass-through.</summary>
    public static Dialect? Find(string host) => Lookup(host)?.Dialect;

    /// <summary>Whether the host also serves browsers, so the proxy decrypts it only for traffic that carries an ai-proxy credential.</summary>
    public static bool IsHarnessOnly(string host) => Lookup(host)?.HarnessOnly == true;
}
