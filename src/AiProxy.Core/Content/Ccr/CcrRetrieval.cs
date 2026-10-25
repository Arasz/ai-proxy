namespace AiProxy.Core.Content.Ccr;

/// <summary>The answer to one <c>proxy_retrieve</c> call: the result text, and an error code when it failed.</summary>
/// <param name="Text">The tool result the model reads.</param>
/// <param name="Error">One of the error codes, or null on success; a zero-match pattern is a success.</param>
public sealed record CcrRetrieval(string Text, string? Error)
{
    /// <summary>The tool's name.</summary>
    public const string Name = "proxy_retrieve";

    /// <summary>The tool definition appended to a request's tools; its bytes never change within a release.</summary>
    public const string Definition = """{"name":"proxy_retrieve","description":"Reads part of a tool result the proxy shortened. Use the id from a [proxy preview ...] line. Ask for lines or a pattern; ask for the whole text only when you need all of it.","input_schema":{"type":"object","properties":{"id":{"type":"string","pattern":"^[0-9a-f]{16}$"},"mode":{"type":"string","enum":["lines","pattern","whole"]},"start":{"type":"integer","minimum":1},"end":{"type":"integer","minimum":1},"pattern":{"type":"string","description":".NET regular expression, case-sensitive"},"context":{"type":"integer","minimum":0,"maximum":10}},"required":["id","mode"],"additionalProperties":false}}""";

    /// <summary>The id has no original: unknown, evicted or never kept.</summary>
    public const string NotKept = "not_kept";

    /// <summary>The line range is outside the original.</summary>
    public const string BadRange = "bad_range";

    /// <summary>The pattern is not a valid non-backtracking .NET regular expression, or it timed out.</summary>
    public const string BadPattern = "bad_pattern";

    /// <summary>The arguments do not follow the tool's schema.</summary>
    public const string BadInput = "bad_input";

    /// <summary>The call came after the last round the loop answers.</summary>
    public const string LimitReached = "limit_reached";

    /// <summary>The answer every call gets once the loop has answered its last round.</summary>
    public static CcrRetrieval Limit() => Fail(LimitReached, "The retrieval limit for this turn is reached. Answer with what you have read.");

    /// <summary>A successful answer.</summary>
    internal static CcrRetrieval Answered(string text) => new(text, null);

    /// <summary>A failed answer: the error code and the reason the model reads.</summary>
    internal static CcrRetrieval Fail(string code, string reason) => new(CcrText.Invariant($"[{Name} error {code}] {reason}"), code);
}
