namespace AiProxy.Core.Content.Ccr;

/// <summary>Names the Chat Completions API uses in requests.</summary>
public static class ChatApi
{
    /// <summary>Property names in a Chat Completions request.</summary>
    public static class Fields
    {
        public const string Model = "model";
        public const string Stream = "stream";
        public const string Tools = "tools";
        public const string ToolChoice = "tool_choice";
        public const string Choices = "n";
        public const string StreamOptions = "stream_options";
        public const string IncludeUsage = "include_usage";
        public const string Function = "function";
        public const string Name = "name";
    }
}
