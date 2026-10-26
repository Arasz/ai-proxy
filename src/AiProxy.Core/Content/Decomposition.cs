namespace AiProxy.Core.Content;

/// <summary>A payload split into its parts, with the template that holds their places.</summary>
/// <param name="RequestId">The request the parts belong to.</param>
/// <param name="Original">The text the parts were cut from.</param>
/// <param name="Template">The original text with every part replaced by its {type_chunkId} placeholder.</param>
/// <param name="Parts">The parts, in the order they appeared.</param>
public sealed record Decomposition(
    string RequestId,
    string Original,
    string Template,
    IReadOnlyList<ContentPart> Parts)
{
    /// <summary>How many parts the payload was split into.</summary>
    public int ChunkCount => Parts.Count;
}