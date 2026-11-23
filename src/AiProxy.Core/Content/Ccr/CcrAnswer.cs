namespace AiProxy.Core.Content.Ccr;

/// <summary>The answer to one <c>proxy_retrieve</c> call: the text found, or the failure the model reads.</summary>
public readonly union CcrAnswer(CcrFound, CcrFailed);

/// <summary>A successful answer; a zero-match pattern is a success.</summary>
/// <param name="Text">The tool result the model reads.</param>
public sealed record CcrFound(string Text);

/// <summary>A failed answer.</summary>
/// <param name="Code">One of the <see cref="CcrRetrieval"/> error codes.</param>
/// <param name="Text">The tool result the model reads.</param>
public sealed record CcrFailed(string Code, string Text);

/// <summary>Reads the parts every <see cref="CcrAnswer"/> case shares.</summary>
public static class CcrAnswerExtensions
{
    extension(CcrAnswer answer)
    {
        /// <summary>The tool result the model reads.</summary>
        public string Text => answer switch
        {
            CcrFailed failed => failed.Text,
            CcrFound found => found.Text,
        };

        /// <summary>The error code, or null for a found answer. Use only where a nullable column or DTO needs it.</summary>
        public string? ErrorCode => answer switch
        {
            CcrFailed failed => failed.Code,
            CcrFound => null,
        };
    }
}
