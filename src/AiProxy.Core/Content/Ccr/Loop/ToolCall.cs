namespace AiProxy.Core.Content.Ccr.Loop;

/// <summary>A <c>tool_use</c> block the model emitted.</summary>
/// <param name="Id">The tool use id its result must name.</param>
/// <param name="Name">The tool's name.</param>
/// <param name="Input">The tool input as JSON.</param>
public sealed record ToolCall(string Id, string Name, string Input)
{
    /// <summary>Whether the proxy answers this call itself.</summary>
    public bool IsRetrieval => Name == CcrRetrieval.Name;
}
