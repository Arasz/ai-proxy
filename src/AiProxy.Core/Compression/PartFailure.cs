namespace AiProxy.Core.Compression;

/// <summary>A part whose reducer threw, so it was forwarded with its original text.</summary>
/// <param name="ChunkId">The part's number within the request, counted from one.</param>
/// <param name="ContentType">The kind of content the part carries, which picked the reducer.</param>
/// <param name="Exception">What the reducer threw.</param>
public sealed record PartFailure(int ChunkId, string ContentType, Exception Exception);
