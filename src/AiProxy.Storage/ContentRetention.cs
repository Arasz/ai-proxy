using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiProxy.Storage;

/// <summary>Deletes the rows that are past their retention.</summary>
public static class ContentRetention
{
    /// <summary>How many rows one delete takes at most.</summary>
    private const int BatchSize = 1000;

    /// <summary>The most bytes one delete takes, so the write lock is held no longer than the request path waits for it.</summary>
    /// <remarks>A single row that is bigger than this goes alone.</remarks>
    public const int BatchBudgetBytes = 16 * 1024 * 1024;

    /// <summary>The query that picks the keys and byte sizes of the oldest expired rows of a table, at most one batch of them.</summary>
    /// <param name="rule">The table to read.</param>
    public static string CandidateQuery(RetentionRule rule)
    {
        var size = rule.Size ?? "0";
        if (rule.ChildTable is null)
        {
            return $"SELECT rowid AS Key, {size} AS Bytes FROM {rule.Table} WHERE {rule.Column} < @Cutoff LIMIT @Batch";
        }

        return $"SELECT {rule.ChildKey} AS Key, {size} AS Bytes FROM {rule.Table} WHERE {rule.Column} < @Cutoff ORDER BY {rule.Column} LIMIT @Batch";
    }

    /// <param name="connection">The connection to delete on.</param>
    extension(SqliteConnection connection)
    {
        /// <summary>Deletes the rows dated before the cutoff, a batch at a time, and says how many went.</summary>
        /// <param name="rules">The tables to evict; a child table's rows go with their parent's.</param>
        /// <param name="cutoff">Rows dated before this are deleted.</param>
        /// <param name="cancellationToken">The token that stops the pass between batches.</param>
        /// <param name="afterBatch">Called after each batch is committed.</param>
        /// <param name="afterPick">Called after a batch's rows are picked and before they are deleted.</param>
        public async Task<int> EvictOlderThanAsync(IReadOnlyList<RetentionRule> rules, DateTimeOffset cutoff, CancellationToken cancellationToken, Action? afterBatch = null, Action? afterPick = null)
        {
            var cutoffText = cutoff.ToString("O", CultureInfo.InvariantCulture);
            var total = 0;
            foreach (var rule in rules)
            {
                Batch batch;
                do
                {
                    batch = await EvictBatchAsync(connection, rule, cutoffText, afterPick, cancellationToken).ConfigureAwait(false);
                    total += batch.Removed;
                    afterBatch?.Invoke();
                }
                while (batch.Parents > 0);
            }

            return total;
        }
    }

    private static async Task<Batch> EvictBatchAsync(SqliteConnection connection, RetentionRule rule, string cutoff, Action? afterPick, CancellationToken cancellationToken)
    {
        var keys = await PickWithinBudgetAsync(connection, rule, cutoff, cancellationToken).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            return new Batch(0, 0);
        }

        afterPick?.Invoke();
        var parameters = new { Keys = keys, Cutoff = cutoff };
        if (rule.ChildTable is null)
        {
            var rows = await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {rule.Table} WHERE rowid IN @Keys AND {rule.Column} < @Cutoff;",
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return new Batch(rows, rows);
        }

        // The children and their parents go in one transaction, so a reader never finds a
        // request whose chunks are gone.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var children = await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM {rule.ChildTable} WHERE {rule.ChildKey} IN @Keys AND {rule.ChildKey} IN (SELECT {rule.ChildKey} FROM {rule.Table} WHERE {rule.ChildKey} IN @Keys AND {rule.Column} < @Cutoff);",
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var parents = await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM {rule.Table} WHERE {rule.ChildKey} IN @Keys AND {rule.Column} < @Cutoff;",
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new Batch(parents, parents + children);
    }

    /// <summary>Reads the oldest expired rows outside any write lock and keeps the first ones that fit the byte budget.</summary>
    private static async Task<List<object>> PickWithinBudgetAsync(SqliteConnection connection, RetentionRule rule, string cutoff, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = CandidateQuery(rule);
        command.Parameters.AddWithValue("@Cutoff", cutoff);
        command.Parameters.AddWithValue("@Batch", BatchSize);

        var keys = new List<object>();
        long bytes = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var size = reader.GetInt64(1);
            if (keys.Count > 0 && bytes + size > BatchBudgetBytes)
            {
                break;
            }

            keys.Add(reader.GetValue(0));
            bytes += size;
        }

        return keys;
    }

    /// <summary>What one delete took out: the rows of the table the rule names, and all rows including children.</summary>
    private sealed record Batch(int Parents, int Removed);
}
