using AiProxy.Core.Content;

namespace AiProxy.Storage;

/// <summary>Writes a record into the content tables, once the queue hands it over.</summary>
public interface IContentWriter
{
    /// <summary>Writes the record and its parts.</summary>
    /// <param name="record">The record to write.</param>
    /// <param name="cancellationToken">The token that stops the write.</param>
    ValueTask PersistAsync(ContentRecord record, CancellationToken cancellationToken);
}