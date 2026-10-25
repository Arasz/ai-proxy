namespace AiProxy.Core.Content;

/// <summary>The string values of a body that carry tool output, found by walking the parsed document.</summary>
/// <param name="Indexes">The position of each tool output string, with the file its tool call read, or null when that is not certain.</param>
/// <param name="Count">How many string values the document has.</param>
internal sealed record ToolOutputIndexes(Dictionary<int, FileRead?> Indexes, int Count);
