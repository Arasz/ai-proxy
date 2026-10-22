namespace AiProxy.Core.Content;

/// <summary>Runs the reducer that a part's kind routes to.</summary>
public interface ICompressionExecutor
{
    /// <summary>Reduces the part, and says what it cost.</summary>
    /// <param name="part">The part to reduce.</param>
    /// <param name="cancellationToken">The token that stops the work.</param>
    ValueTask<CompressedPart> ExecuteAsync(ContentPart part, CancellationToken cancellationToken);
}
