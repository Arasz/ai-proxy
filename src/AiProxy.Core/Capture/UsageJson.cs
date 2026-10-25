namespace AiProxy.Core.Capture;

/// <summary>The property names and event types of the usage an Anthropic response carries.</summary>
internal static class UsageJson
{
    public const string Usage = "usage";
    public const string Type = "type";
    public const string Message = "message";
    public const string InputTokens = "input_tokens";
    public const string OutputTokens = "output_tokens";
    public const string CacheCreationInputTokens = "cache_creation_input_tokens";
    public const string CacheReadInputTokens = "cache_read_input_tokens";

    public const string MessageStart = "message_start";
    public const string MessageDelta = "message_delta";
}
