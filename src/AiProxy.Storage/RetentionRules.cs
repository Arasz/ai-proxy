namespace AiProxy.Storage;

/// <summary>A table the retention pass evicts, and the column that dates its rows.</summary>
/// <param name="Table">The table the rows are deleted from.</param>
/// <param name="Column">The column holding the time the row is dated by.</param>
/// <param name="ChildTable">A table whose rows go with their parent row, when there is one.</param>
/// <param name="ChildKey">The column that holds the parent's key, named alike in both tables.</param>
/// <param name="Size">An SQL expression for the bytes a row of the table, and its child rows, hold; rows weigh nothing when it is null.</param>
public sealed record RetentionRule(string Table, string Column, string? ChildTable = null, string? ChildKey = null, string? Size = null);

/// <summary>Every table the proxy keeps in the content and capture files, and what retention does with it.</summary>
public static class RetentionRules
{
    private const string RequestSize = """
                                       octet_length(original) + octet_length(compressed) + (
                                           SELECT COALESCE(SUM(octet_length(original_text) + octet_length(compressed_text)), 0)
                                           FROM content_chunk WHERE content_chunk.request_id = content_request.request_id)
                                       """;

    /// <summary>The content file's tables that are evicted.</summary>
    public static IReadOnlyList<RetentionRule> Content { get; } =
    [
        new("content_request", "created_at", "content_chunk", "request_id", RequestSize),
        new("content_queue_dead", "failed_at", Size: "octet_length(payload) + octet_length(error)"),
        new("ccr_original", "created_at", Size: "octet_length(text)"),
        new("ccr_thread", "created_at")
    ];

    /// <summary>The capture file's tables that are evicted.</summary>
    public static IReadOnlyList<RetentionRule> Capture { get; } =
    [
        new("capture_request", "captured_at"),
        new("capture_response", "completed_at")
    ];

    /// <summary>The tables that are kept on purpose: the queue holds work not yet done, and SQLite owns the sequence table.</summary>
    public static IReadOnlyList<string> Exempt { get; } = ["content_queue", "sqlite_sequence"];
}
