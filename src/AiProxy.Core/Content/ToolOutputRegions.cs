namespace AiProxy.Core.Content;

/// <summary>The regions the raw bytes of a body yield at the positions of tool output strings.</summary>
/// <param name="Regions">The regions found.</param>
/// <param name="Count">How many string tokens the bytes have.</param>
internal sealed record ToolOutputRegions(IReadOnlyList<ContentRegion> Regions, int Count);