namespace AiProxy.Core.Content;

/// <summary>Splits a payload into the parts that carry different kinds of content.</summary>
public interface IDecompositor
{
    /// <summary>Splits the content, and returns the template that holds the parts' places.</summary>
    /// <param name="content">The text to split.</param>
    /// <param name="requestId">The request the parts belong to.</param>
    /// <param name="filePath">The whole file the content was read from, or null when that is not certain.</param>
    Decomposition Decompose(string content, string requestId, string? filePath = null);
}
