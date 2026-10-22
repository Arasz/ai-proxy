namespace AiProxy.Core.Content;

/// <summary>One part of a decomposed payload, with the kind that decides its reducer.</summary>
/// <param name="RequestId">The request the part belongs to.</param>
/// <param name="ChunkId">The part's number within the request, counted from one.</param>
/// <param name="Type">The kind of content the part carries.</param>
/// <param name="Text">The part's text, exactly as the client sent it.</param>
public sealed record ContentPart(string RequestId, int ChunkId, ContentType Type, string Text);
