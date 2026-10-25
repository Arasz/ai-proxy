namespace AiProxy.Core.Content;

/// <summary>The reduced text, with the path of the reducer that produced it.</summary>
/// <param name="Text">The reduced text.</param>
/// <param name="Path">The path of the reducer, which the store keeps next to the text.</param>
/// <param name="RetrievalId">The id the text names for <c>proxy_retrieve</c>; the part's original must be kept under it before the text is sent.</param>
public sealed record HandledContent(string Text, string Path, string? RetrievalId = null);
