namespace AiProxy.Core.Content;

/// <summary>The property names and type values that tool calls and tool results use in a request body.</summary>
internal static class ToolCallJson
{
    public const string Type = "type";
    public const string Role = "role";
    public const string Content = "content";
    public const string Output = "output";
    public const string Result = "result";
    public const string Text = "text";
    public const string Name = "name";
    public const string Id = "id";
    public const string Input = "input";
    public const string Arguments = "arguments";
    public const string Function = "function";
    public const string FunctionResponse = "functionResponse";
    public const string ToolCalls = "tool_calls";
    public const string ToolUseId = "tool_use_id";
    public const string CallId = "call_id";
    public const string ToolCallId = "tool_call_id";
    public const string IsError = "is_error";
    public const string AllowLarge = "allow_large";

    public const string FilePathSnake = "file_path";
    public const string FilePathCamel = "filePath";
    public const string Path = "path";

    public const string ToolResult = "tool_result";
    public const string ToolUse = "tool_use";
    public const string FunctionCall = "function_call";
    public const string FunctionCallOutput = "function_call_output";
    public const string Tool = "tool";
}