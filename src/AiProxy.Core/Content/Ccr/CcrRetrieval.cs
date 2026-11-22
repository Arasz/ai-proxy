using System.Text.Json.Nodes;

namespace AiProxy.Core.Content.Ccr;

/// <summary>The <c>proxy_retrieve</c> tool: its name, definitions, error codes and the answers it gives.</summary>
public static class CcrRetrieval
{
    /// <summary>The tool's name.</summary>
    public const string Name = "proxy_retrieve";

    /// <summary>The tool definition appended to a request's tools; its bytes never change within a release.</summary>
    public const string Definition =
        """{"name":"proxy_retrieve","description":"Reads part of a tool result the proxy shortened. Use the id from a [proxy preview ...] line. Ask for lines or a pattern; ask for the whole text only when you need all of it.","input_schema":{"type":"object","properties":{"id":{"type":"string","pattern":"^[0-9a-f]{16}$"},"mode":{"type":"string","enum":["lines","pattern","whole"]},"start":{"type":"integer","minimum":1},"end":{"type":"integer","minimum":1},"pattern":{"type":"string","description":".NET regular expression, case-sensitive"},"context":{"type":"integer","minimum":0,"maximum":10}},"required":["id","mode"],"additionalProperties":false}}""";

    /// <summary>The tool definition appended to a Chat Completions request's tools: <see cref="Definition" /> as a function tool.</summary>
    public static readonly string ChatDefinition = AsFunctionTool(Definition);

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
    public static CcrAnswer Limit() => Fail(LimitReached, "The retrieval limit for this turn is reached. Answer with what you have read.");

    private static string AsFunctionTool(string definition)
    {
        var tool = JsonNode.Parse(definition)!.AsObject();
        var function = new JsonObject { ["name"] = tool["name"]!.DeepClone(), ["description"] = tool["description"]!.DeepClone(), ["parameters"] = tool["input_schema"]!.DeepClone() };
        return new JsonObject { ["type"] = "function", ["function"] = function }.ToJsonString(MessagesApi.Json);
    }

    /// <summary>A successful answer.</summary>
    internal static CcrAnswer Answered(string text) => new CcrFound(text);

    /// <summary>A failed answer: the error code and the reason the model reads.</summary>
    internal static CcrAnswer Fail(string code, string reason) => new CcrFailed(code, CcrText.Invariant($"[{Name} error {code}] {reason}"));
}