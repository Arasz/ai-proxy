using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Storage;

/// <summary>Deletes the rows that are past their retention.</summary>
public static class ContentRetention
{
    /// <summary>How many rows one delete takes, so the write lock is held for a short time.</summary>
    private const int BatchSize = 1000;

    /// <param name="connection">The connection to delete on.</param>
    extension(SqliteConnection connection)
    {
        /// <summary>Deletes the rows dated before the cutoff, a batch at a time, and says how many went.</summary>
        /// <param name="rules">The tables to evict; a child table's rows go with their parent's.</param>
        /// <param name="cutoff">Rows dated before this are deleted.</param>
        /// <param name="cancellationToken">The token that stops the pass between batches.</param>
        public async Task<int> EvictOlderThanAsync(IReadOnlyList<RetentionRule> rules, DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(rules);

            var cutoffText = cutoff.ToString("O", CultureInfo.InvariantCulture);
            var total = 0;
            foreach (var rule in rules)
            {
                Batch batch;
                do
                {
                    batch = await EvictBatchAsync(connection, rule, cutoffText, cancellationToken).ConfigureAwait(false);
                    total += batch.Removed;
                }
                while (batch.Parents == BatchSize);
            }

            return total;
        }
    }

    private static async Task<Batch> EvictBatchAsync(SqliteConnection connection, RetentionRule rule, string cutoff, CancellationToken cancellationToken)
    {
        var parameters = new { Cutoff = cutoff, Batch = BatchSize };
        if (rule.ChildTable is null)
        {
            var rows = await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {rule.Table} WHERE rowid IN (SELECT rowid FROM {rule.Table} WHERE {rule.Column} < @Cutoff LIMIT @Batch);",
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return new Batch(rows, rows);
        }

        var oldest = $"SELECT {rule.ChildKey} FROM {rule.Table} WHERE {rule.Column} < @Cutoff ORDER BY {rule.Column}, {rule.ChildKey} LIMIT @Batch";

        // The children and their parents go in one transaction, so a reader never finds a
        // request whose chunks are gone.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var children = await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM {rule.ChildTable} WHERE {rule.ChildKey} IN ({oldest});",
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var parents = await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM {rule.Table} WHERE {rule.ChildKey} IN ({oldest});",
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new Batch(parents, parents + children);
    }

    /// <summary>What one delete took out: the rows of the table the rule names, and all rows including children.</summary>
    private sealed record Batch(int Parents, int Removed);
}
