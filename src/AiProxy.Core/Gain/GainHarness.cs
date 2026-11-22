namespace AiProxy.Core.Gain;

/// <summary>Maps a ledger provider and request host to the harness its traffic is reported under; the ledger does not record the client itself.</summary>
public static class GainHarness
{
    public const string Dimension = "harness";
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string OpenRouter = "openrouter";
    public const string OpenAi = "openai";
    public const string OpenAiHostUnknown = "openai (host unknown)";
    public const string Other = "other";

    /// <summary>Every harness, in display order.</summary>
    public static readonly IReadOnlyList<string> Names = [Claude, Codex, OpenRouter, OpenAi, OpenAiHostUnknown, Other];

    /// <summary>
    /// The harness of a request: Anthropic is Claude; an OpenAI-dialect request is told apart by host, and one without a host
    /// (written before the ledger kept it) is <see cref="OpenAiHostUnknown"/>; anything else is <see cref="Other"/>.
    /// </summary>
    public static string Of(string? provider, string? host) =>
        provider switch
        {
            "anthropic" => Claude,
            "openai" => OfOpenAiHost(host),
            _ => Other
        };

    private static string OfOpenAiHost(string? host)
    {
        host = host?.TrimEnd('.');
        return host switch
        {
            null or "" => OpenAiHostUnknown,
            _ when host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) => Codex,
            _ when IsOrSubdomainOf(host, "openrouter.ai") => OpenRouter,
            _ when IsOrSubdomainOf(host, "api.openai.com") => OpenAi,
            _ => Other
        };
    }

    private static bool IsOrSubdomainOf(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);
}
