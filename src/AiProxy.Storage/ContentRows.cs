namespace AiProxy.Storage;

/// <summary>The row of the content request table.</summary>
/// <param name="RequestId">The request the row belongs to.</param>
/// <param name="CreatedAt">When the record was made.</param>
/// <param name="Original">The body the client sent.</param>
/// <param name="Compressed">The body the proxy forwarded.</param>
/// <param name="Ratio">The compressed length over the original length.</param>
/// <param name="Path">The paths of the reducers that ran.</param>
/// <param name="ChunkCount">How many parts the request was split into.</param>
public sealed record ContentRequestRow(
    string RequestId,
    string CreatedAt,
    string Original,
    string Compressed,
    double Ratio,
    string Path,
    long ChunkCount);

/// <summary>The row of the content chunk table.</summary>
/// <param name="ChunkId">The part's number within the request.</param>
/// <param name="ContentType">The kind of content the part carries.</param>
/// <param name="OriginalText">The text the reducer started from.</param>
/// <param name="CompressedText">The reduced text.</param>
/// <param name="Path">The path of the reducer that produced the text.</param>
/// <param name="Ratio">The reduced length over the original length.</param>
public sealed record ContentChunkRow(
    long ChunkId,
    string ContentType,
    string OriginalText,
    string CompressedText,
    string Path,
    double Ratio);
