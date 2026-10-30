namespace AiProxy.Core.Providers;

/// <summary>Tells a provider's model endpoints from its other API and telemetry traffic.</summary>
public static class ProviderEndpoints
{
    private static readonly string[] OpenAiEndings = ["/chat/completions", "/completions", "/responses", "/messages"];
    private static readonly string[] GeminiEndings = [":generateContent", ":streamGenerateContent"];

    /// <summary>Whether a request is a model call: the savings totals count it, anything else is other traffic.</summary>
    public static bool IsModelCall(Dialect dialect, string method, string path)
    {
        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var trimmed = path.TrimEnd('/');
        return dialect switch
        {
            Dialect.Anthropic => trimmed.Equals("/v1/messages", StringComparison.OrdinalIgnoreCase),
            Dialect.OpenAi => EndsWithAny(trimmed, OpenAiEndings),
            Dialect.Gemini => EndsWithAny(trimmed, GeminiEndings),
            _ => false
        };
    }

    private static bool EndsWithAny(string path, string[] endings)
    {
        return endings.Any(ending => path.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
    }
}
