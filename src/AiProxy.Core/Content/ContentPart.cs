using AiProxy.Core.Content.Ccr;

namespace AiProxy.Core.Content;

/// <summary>One part of a decomposed payload, with the kind that decides its reducer.</summary>
/// <param name="RequestId">The request the part belongs to.</param>
/// <param name="ChunkId">The part's number within the request, counted from one.</param>
/// <param name="Type">The kind of content the part carries.</param>
/// <param name="Text">The part's text, exactly as the client sent it.</param>
/// <param name="FilePath">The whole file the part was read from, or null when that is not certain.</param>
/// <param name="Language">The source language named by the file's extension; null when the kind was guessed.</param>
/// <param name="Ccr">The request's CCR mode; a reducer that drops text the model may need sends it only when this is not off.</param>
public sealed record ContentPart(
    string RequestId,
    int ChunkId,
    ContentType Type,
    string Text,
    string? FilePath = null,
    string? Language = null,
    CcrMode Ccr = CcrMode.Off);