namespace AiProxy.Core.Content;

/// <summary>A stretch of the request body that carries the output of a tool.</summary>
/// <param name="Start">The offset of the region in the body.</param>
/// <param name="Length">The length of the region in bytes.</param>
/// <param name="Text">The text of the region.</param>
/// <param name="FilePath">The whole file the tool call read, or null when that is not certain.</param>
public sealed record ContentRegion(int Start, int Length, string Text, string? FilePath = null);
