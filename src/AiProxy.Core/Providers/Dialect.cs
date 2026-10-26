namespace AiProxy.Core.Providers;

/// <summary>The LLM provider dialect a request speaks.</summary>
public enum Dialect
{
    /// <summary>Not confidently one of the known dialects.</summary>
    Unknown,
    Anthropic,
    OpenAi,
    Gemini
}