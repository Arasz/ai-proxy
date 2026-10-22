namespace AiProxy.Core.Content;

/// <summary>The reduced text, with the path of the reducer that produced it.</summary>
/// <param name="Text">The reduced text.</param>
/// <param name="Path">The path of the reducer, which the store keeps next to the text.</param>
public sealed record HandledContent(string Text, string Path);
