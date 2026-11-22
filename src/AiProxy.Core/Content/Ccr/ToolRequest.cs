namespace AiProxy.Core.Content.Ccr;

/// <summary>A request body with <c>proxy_retrieve</c> added to its tools, ready to forward.</summary>
/// <param name="Body">The body to forward.</param>
/// <param name="ToolCharacters">How many characters the tool definition added; 0 when the tool was already there.</param>
public sealed record ToolRequest(byte[] Body, int ToolCharacters);
