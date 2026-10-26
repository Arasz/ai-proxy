using System.Text.Json.Serialization;

namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>Whether a tool call the proxy answered succeeded.</summary>
public enum ToolOutcome
{
    /// <summary>The model reads the result as an answer.</summary>
    Succeeded,

    /// <summary>The model reads the result as an error.</summary>
    Failed
}

/// <summary>A <c>tool_result</c> block the proxy writes for a call it answered or deferred; build it with the factories.</summary>
/// <param name="ToolUseId">The call it answers.</param>
/// <param name="Content">The text the model reads.</param>
/// <param name="Outcome">Whether the call succeeded.</param>
public sealed record ToolResult(string ToolUseId, string Content, [property: JsonIgnore] ToolOutcome Outcome)
{
    /// <summary>The block type.</summary>
    [JsonPropertyOrder(-1)]
    public string Type => MessagesApi.Blocks.ToolResult;

    /// <summary>Written as <c>is_error: true</c> only for a failed call.</summary>
    public bool IsError => Outcome == ToolOutcome.Failed;

    /// <summary>The result of a call the proxy answered.</summary>
    public static ToolResult Of(string toolUseId, CcrRetrieval answer)
    {
        return new ToolResult(toolUseId, answer.Text, answer.Error is null ? ToolOutcome.Succeeded : ToolOutcome.Failed);
    }

    /// <summary>The result of a client call the proxy did not run.</summary>
    public static ToolResult Deferred(string toolUseId)
    {
        return new ToolResult(toolUseId, RetrievalLoop.DeferredCall, ToolOutcome.Failed);
    }
}

/// <summary>A message the proxy adds to a follow-up.</summary>
/// <param name="Role">user or assistant.</param>
/// <param name="Content">The blocks.</param>
public sealed record Turn<T>(string Role, T Content);