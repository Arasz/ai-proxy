namespace AiProxy.Core.Content;

/// <summary>Keeps what the proxy compressed, so the original can be served back later.</summary>
public interface IContentStore
{
    /// <summary>Keeps a record.</summary>
    /// <param name="record">The record to keep.</param>
    /// <param name="cancellationToken">The token that stops the write.</param>
    ValueTask StoreAsync(ContentRecord record, CancellationToken cancellationToken);

    /// <summary>Reads a record back, or null when the request id was never kept.</summary>
    /// <param name="requestId">The request to look for.</param>
    /// <param name="cancellationToken">The token that stops the read.</param>
    ValueTask<ContentRecord?> GetAsync(string requestId, CancellationToken cancellationToken);
}