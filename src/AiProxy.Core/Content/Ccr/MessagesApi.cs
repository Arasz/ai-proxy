using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiProxy.Core.Content.Ccr;

/// <summary>The names the Anthropic Messages API uses, and the JSON settings the proxy writes its own parts of it with.</summary>
public static class MessagesApi
{
    /// <summary>Writes snake_case names, leaves out default values, and escapes only what JSON requires.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Server-sent event names.</summary>
    public static class Events
    {
        public const string MessageStart = "message_start";
        public const string ContentBlockStart = "content_block_start";
        public const string ContentBlockDelta = "content_block_delta";
        public const string ContentBlockStop = "content_block_stop";
        public const string MessageDelta = "message_delta";
        public const string MessageStop = "message_stop";
        public const string Ping = "ping";
        public const string Error = "error";
    }

    /// <summary>Content block types.</summary>
    public static class Blocks
    {
        public const string ToolUse = "tool_use";
        public const string ToolResult = "tool_result";
    }

    /// <summary>Delta types inside <c>content_block_delta</c>.</summary>
    public static class Deltas
    {
        public const string Text = "text_delta";
        public const string Thinking = "thinking_delta";
        public const string Signature = "signature_delta";
        public const string InputJson = "input_json_delta";
        public const string Citations = "citations_delta";
    }

    /// <summary>Property names in events, blocks and requests.</summary>
    public static class Fields
    {
        public const string Type = "type";
        public const string Index = "index";
        public const string Message = "message";
        public const string ContentBlock = "content_block";
        public const string Delta = "delta";
        public const string Id = "id";
        public const string Name = "name";
        public const string Input = "input";
        public const string Text = "text";
        public const string Thinking = "thinking";
        public const string Signature = "signature";
        public const string PartialJson = "partial_json";
        public const string Citation = "citation";
        public const string Citations = "citations";
        public const string StopReason = "stop_reason";
        public const string Usage = "usage";
        public const string OutputTokens = "output_tokens";
        public const string Messages = "messages";
        public const string Tools = "tools";
        public const string ToolChoice = "tool_choice";
        public const string Stream = "stream";
        public const string Thread = "thread";
        public const string Diagnostics = "diagnostics";
        public const string PreviousMessageId = "previous_message_id";
    }

    /// <summary>Thread types of a request's <c>thread</c> object.</summary>
    public static class ThreadTypes
    {
        public const string Create = "create";
        public const string Continue = "continue";
    }

    /// <summary>Stop reasons.</summary>
    public static class StopReasons
    {
        public const string ToolUse = "tool_use";
    }

    /// <summary>Message roles.</summary>
    public static class Roles
    {
        public const string User = "user";
        public const string Assistant = "assistant";
    }

    /// <summary>Error types of an <c>error</c> event.</summary>
    public static class Errors
    {
        public const string Api = "api_error";
        public const string Overloaded = "overloaded_error";
    }
}
