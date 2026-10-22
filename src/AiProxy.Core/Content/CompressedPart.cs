namespace AiProxy.Core.Content;

/// <summary>A part after its reducer ran, with the path that produced it.</summary>
/// <param name="ChunkId">The part's number within the request, counted from one.</param>
/// <param name="Type">The kind of content the part carries.</param>
/// <param name="Text">The reduced text.</param>
/// <param name="OriginalText">The text the reducer started from.</param>
/// <param name="Path">The path of the reducer that produced the text.</param>
/// <param name="Ratio">The reduced length over the original length.</param>
public sealed record CompressedPart(
    int ChunkId,
    ContentType Type,
    string Text,
    string OriginalText,
    string Path,
    double Ratio);
