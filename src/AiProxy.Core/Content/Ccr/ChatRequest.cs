using System.Text;
using System.Text.Json;
using AiProxy.Core.Json;

namespace AiProxy.Core.Content.Ccr;

/// <summary>What CCR reads from a Chat Completions request body, and the byte-exact edits it makes to one.</summary>
/// <param name="Streamed">Whether <c>stream</c> is true.</param>
/// <param name="HasTools">Whether the request defines at least one tool.</param>
/// <param name="Model">The model id.</param>
/// <param name="Choices">How many choices the request asks for (<c>n</c>).</param>
/// <param name="IncludeUsage">Whether the client asked for a usage chunk.</param>
/// <param name="CanRetrieve">Whether <c>tool_choice</c> leaves the model free to call <c>proxy_retrieve</c>.</param>
public sealed record ChatRequest(bool Streamed, bool HasTools, string Model, int Choices, bool IncludeUsage, bool CanRetrieve)
{
    /// <summary>The only host whose models CCR serves through Chat Completions.</summary>
    public const string OpenRouterHost = "openrouter.ai";

    /// <summary>The path ending of the Chat Completions endpoint.</summary>
    public const string PathEnding = "/chat/completions";

    /// <summary>The model id prefixes whose upstream provider the proxy can name; a router or an unknown prefix gets no CCR.</summary>
    public static readonly IReadOnlyList<string> KnownPrefixes = ["anthropic/", "openai/", "google/", "deepseek/"];

    private const string StreamOptions = "stream_options";
    private const string UsageOption = "include_usage";
    private const string NoToolChoice = "none";
    private static readonly byte[] DefinitionBytes = Encoding.UTF8.GetBytes(CcrRetrieval.ChatDefinition);
    private static readonly byte[] UsageBytes = Encoding.UTF8.GetBytes("\"stream_options\":{\"include_usage\":true}");

    /// <summary>Whether CCR can serve the request: streamed, with tools, one choice, a known model, and a tool choice that allows <c>proxy_retrieve</c>.</summary>
    public bool Qualifies => Streamed && HasTools && Choices <= 1 && CanRetrieve && KnownPrefixes.Any(prefix => Model.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Whether the path is a Chat Completions endpoint.</summary>
    public static bool IsChatPath(string path)
    {
        return path.TrimEnd('/').EndsWith(PathEnding, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the request, or returns null when the body is not a JSON object.</summary>
    public static ChatRequest? Read(ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.TryParse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        var streamed = root.TryGetProperty(MessagesApi.Fields.Stream, out var stream) && stream.ValueKind == JsonValueKind.True;
        var hasTools = root.TryGetProperty(MessagesApi.Fields.Tools, out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0;
        var model = root.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String ? modelValue.GetString() ?? "" : "";
        var choices = root.TryGetProperty("n", out var n) && n.TryGetInt32(out var count) ? count : 1;
        var includeUsage = root.TryGetProperty(StreamOptions, out var options) && options.ValueKind == JsonValueKind.Object
                           && options.TryGetProperty(UsageOption, out var usage) && usage.ValueKind == JsonValueKind.True;
        var canRetrieve = !root.TryGetProperty(MessagesApi.Fields.ToolChoice, out var choice) || AllowsRetrieval(choice);
        return new ChatRequest(streamed, hasTools, model, choices, includeUsage, canRetrieve);
    }

    /// <summary>
    ///     Appends <c>proxy_retrieve</c> as the last tool (unless it is there) and asks the upstream for a usage chunk,
    ///     leaving every other byte as it was so the provider's prompt cache keeps matching.
    /// </summary>
    public static byte[] Prepare(ReadOnlySpan<byte> body)
    {
        var scan = Scan(body);
        var edits = new List<(int At, byte[] Text)>();
        if (scan.ToolsEnd >= 0 && !scan.HasRetrieval)
        {
            edits.Add((scan.ToolsEnd, [(byte)',', .. DefinitionBytes]));
        }

        if (!scan.HasStreamOptions && scan.ObjectEnd >= 0)
        {
            edits.Add((scan.ObjectEnd, [(byte)',', .. UsageBytes]));
        }

        var result = body.ToArray();
        foreach (var (at, text) in edits.OrderByDescending(static edit => edit.At))
        {
            result = [.. result[..at], .. text, .. result[at..]];
        }

        return scan.HasStreamOptions && !scan.UsageRequested ? WithUsage(result) : result;
    }

    private static bool AllowsRetrieval(JsonElement choice)
    {
        return choice.ValueKind switch
        {
            JsonValueKind.String => choice.GetString() != NoToolChoice,
            JsonValueKind.Object => choice.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object
                                    && function.TryGetProperty("name", out var name) && name.GetString() == CcrRetrieval.Name,
            _ => true
        };
    }

    private static byte[] WithUsage(byte[] body)
    {
        var request = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        var options = request[StreamOptions] as System.Text.Json.Nodes.JsonObject ?? [];
        options[UsageOption] = true;
        request[StreamOptions] = options;
        return JsonSerializer.SerializeToUtf8Bytes(request, MessagesApi.Json);
    }

    private static BodyScan Scan(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body);
        var scan = new BodyScan();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
            {
                scan.ObjectEnd = (int)reader.TokenStartIndex;
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
            {
                continue;
            }

            if (reader.ValueTextEquals(MessagesApi.Fields.Tools))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    var start = (int)reader.TokenStartIndex;
                    reader.Skip();
                    scan.ToolsEnd = (int)reader.TokenStartIndex;
                    scan.HasRetrieval = HasTool(body[start..(scan.ToolsEnd + 1)]);
                }
            }
            else if (reader.ValueTextEquals(StreamOptions))
            {
                scan.HasStreamOptions = true;
                reader.Read();
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                scan.UsageRequested = reader.TokenType == JsonTokenType.EndObject && IncludesUsage(body[start..((int)reader.TokenStartIndex + 1)]);
            }
        }

        return scan;
    }

    private static bool HasTool(ReadOnlySpan<byte> tools)
    {
        using var document = JsonDocument.Parse(tools.ToArray());
        return document.RootElement.EnumerateArray().Any(static tool => tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("function", out var function)
            && function.ValueKind == JsonValueKind.Object && function.TryGetProperty("name", out var name) && name.GetString() == CcrRetrieval.Name);
    }

    private static bool IncludesUsage(ReadOnlySpan<byte> options)
    {
        using var document = JsonDocument.Parse(options.ToArray());
        return document.RootElement.TryGetProperty(UsageOption, out var usage) && usage.ValueKind == JsonValueKind.True;
    }

    private sealed class BodyScan
    {
        public int ToolsEnd { get; set; } = -1;

        public int ObjectEnd { get; set; } = -1;

        public bool HasRetrieval { get; set; }

        public bool HasStreamOptions { get; set; }

        public bool UsageRequested { get; set; }
    }
}
