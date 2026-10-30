namespace AiProxy.Storage;

/// <summary>A table the retention pass evicts, and the column that dates its rows.</summary>
/// <param name="Table">The table the rows are deleted from.</param>
/// <param name="Column">The column holding the time the row is dated by.</param>
/// <param name="ChildTable">A table whose rows go with their parent row, when there is one.</param>
/// <param name="ChildKey">The column that holds the parent's key, named alike in both tables.</param>
public sealed record RetentionRule(string Table, string Column, string? ChildTable = null, string? ChildKey = null);

/// <summary>Every table the proxy keeps in the content and capture files, and what retention does with it.</summary>
public static class RetentionRules
{
    /// <summary>The content file's tables that are evicted.</summary>
    public static IReadOnlyList<RetentionRule> Content { get; } =
    [
        new("content_request", "created_at", "content_chunk", "request_id"),
        new("content_queue_dead", "failed_at"),
        new("ccr_original", "created_at"),
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
