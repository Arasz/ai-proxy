using System.Globalization;
using AiProxy.Core.Content.Ccr;
using Dapper;

namespace AiProxy.Storage;

/// <summary>Keeps CCR originals and the thread ledger in the content database, written directly in the request path.</summary>
/// <param name="options">Where the content database lives.</param>
/// <param name="time">Stamps rows and decides which are old enough to evict.</param>
public sealed class SqliteOriginalStore(ContentStoreOptions options, TimeProvider time) : IOriginalStore
{
    /// <summary>How long originals are kept after they are written, and ledger rows after they are last read or written.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    /// <inheritdoc />
    public async ValueTask KeepAsync(KeptOriginal original, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await connection.ExecuteAsync(Command(
            "INSERT OR IGNORE INTO ccr_original (id, text, line_count, created_at) VALUES (@Id, @Text, @LineCount, @CreatedAt);",
            new { original.Id, original.Text, original.LineCount, CreatedAt = Now() },
            cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<string?> FindAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        return await connection.QuerySingleOrDefaultAsync<string>(Command(
            "SELECT text FROM ccr_original WHERE id = @Id;",
            new { Id = id },
            cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<string?> FindThreadAsync(string messageId, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        return await connection.QuerySingleOrDefaultAsync<string>(Command(
            "UPDATE ccr_thread SET created_at = @Now WHERE message_id = @MessageId RETURNING continue_from;",
            new { MessageId = messageId, Now = Now() },
            cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask LinkThreadAsync(string messageId, string continueFrom, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await connection.ExecuteAsync(Command(
            """
            INSERT INTO ccr_thread (message_id, continue_from, created_at) VALUES (@MessageId, @ContinueFrom, @CreatedAt)
            ON CONFLICT (message_id) DO UPDATE SET continue_from = excluded.continue_from, created_at = excluded.created_at;
            """,
            new { MessageId = messageId, ContinueFrom = continueFrom, CreatedAt = Now() },
            cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Deletes the originals and ledger rows older than <see cref="Retention"/>, and says how many went.</summary>
    public async Task<int> EvictAsync(CancellationToken cancellationToken)
    {
        await using var connection = ContentSchema.Open(options.ResolvedDatabasePath);
        connection.Ensure();
        var cutoff = (time.GetUtcNow() - Retention).ToString("O", CultureInfo.InvariantCulture);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ccr_original WHERE created_at < @Cutoff; DELETE FROM ccr_thread WHERE created_at < @Cutoff;",
            new { Cutoff = cutoff },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private Microsoft.Data.Sqlite.SqliteConnection Open()
    {
        var connection = ContentSchema.Open(options.ResolvedDatabasePath);
        connection.Ensure(ContentSchema.RequestCommandTimeoutSeconds);
        return connection;
    }

    private string Now() => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, commandTimeout: ContentSchema.RequestCommandTimeoutSeconds, cancellationToken: cancellationToken);
}
