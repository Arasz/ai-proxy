namespace AiProxy.Core.Content.Ccr;

/// <summary>A large tool result shortened to original lines, with each hidden run named by its line range.</summary>
/// <param name="Id">The first 16 hex characters of the original's UTF-8 SHA-256.</param>
/// <param name="Text">The preview shown in place of the original.</param>
/// <param name="ShownLines">The count of shown original lines, including a cut first line.</param>
/// <param name="TotalLines">The original's line count.</param>
public sealed record CcrPreview(string Id, string Text, int ShownLines, int TotalLines)
{
    /// <summary>The marker that also prevents previewing a region twice.</summary>
    public const string Marker = "[proxy preview ";
}
