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
    /// <summary>The path ending of the Chat Completions endpoint.</summary>
    private const string PathEnding = "/chat/completions";

    /// <summary>The model id prefixes whose upstream provider the proxy can name; a router or an unknown prefix gets no CCR.</summary>
    private static readonly IReadOnlyList<string> KnownPrefixes = ["anthropic/", "openai/", "google/", "deepseek/"];

    private const string StreamOptions = ChatApi.Fields.StreamOptions;
    private const string UsageOption = ChatApi.Fields.IncludeUsage;
    private const string NoToolChoice = "none";
    private const int UnreadableChoices = 2;
    private static readonly byte[] DefinitionBytes = Encoding.UTF8.GetBytes(CcrRetrieval.ChatDefinition);
    private static readonly byte[] TrueBytes = [.. "true"u8];
    private static readonly byte[] UsageMemberBytes = [.. "\"include_usage\":true"u8];
    private static readonly byte[] OptionsBytes = [.. "{\"include_usage\":true}"u8];
    private static readonly byte[] UsageBytes = [.. "\"stream_options\":{\"include_usage\":true}"u8];

    /// <summary>Whether CCR can serve the request: streamed, with tools, one choice, a known model, and a tool choice that allows <c>proxy_retrieve</c>.</summary>
    public bool Qualifies => Streamed && HasTools && Choices <= 1 && CanRetrieve && KnownPrefixes.Any(prefix => Model.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Whether the path is a Chat Completions endpoint.</summary>
    public static bool IsChatPath(string path) => path.TrimEnd('/').EndsWith(PathEnding, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the request, or returns null when the body is not a JSON object.</summary>
    public static ChatRequest? Read(ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.TryParse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        var streamed = root.TryGetProperty(ChatApi.Fields.Stream, out var stream) && stream.ValueKind == JsonValueKind.True;
        var hasTools = root.TryGetProperty(ChatApi.Fields.Tools, out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0;
        var model = root.TryGetProperty(ChatApi.Fields.Model, out var modelValue) && modelValue.ValueKind == JsonValueKind.String ? modelValue.GetString() ?? "" : "";
        var choices = ChoicesOf(root);
        var includeUsage = root.TryGetProperty(StreamOptions, out var options) && options.ValueKind == JsonValueKind.Object
                           && options.TryGetProperty(UsageOption, out var usage) && usage.ValueKind == JsonValueKind.True;
        var canRetrieve = !root.TryGetProperty(ChatApi.Fields.ToolChoice, out var choice) || AllowsRetrieval(choice);
        return new ChatRequest(streamed, hasTools, model, choices, includeUsage, canRetrieve);
    }

    /// <summary>
    ///     Appends <c>proxy_retrieve</c> as the last tool (unless it is there) and asks the upstream for a usage chunk,
    ///     leaving every other byte as it was so the provider's prompt cache keeps matching. Returns null when the body
    ///     has no non-empty <c>tools</c> array or is not a whole JSON object: there is nothing to append to.
    /// </summary>
    public static ToolRequest? WithTool(ReadOnlySpan<byte> body)
    {
        if (Scan(body) is not { ObjectEnd: >= 0, ToolsEnd: >= 0, ToolCount: > 0 } scan)
        {
            return null;
        }

        var toolEdit = ToolEdit(scan);
        BodyEdit?[] edits = [toolEdit, UsageEdit(scan)];
        return new ToolRequest(Apply(body, edits), toolEdit?.Text.Length ?? 0);
    }

    private static BodyEdit? ToolEdit(BodyScan scan) => scan.HasRetrieval ? null : BodyEdit.Insert(scan.ToolsEnd, [(byte)',', .. DefinitionBytes]);

    private static BodyEdit? UsageEdit(BodyScan scan)
    {
        if (!scan.HasStreamOptions)
        {
            return BodyEdit.Insert(scan.ObjectEnd, [(byte)',', .. UsageBytes]);
        }

        if (scan.UsageRequested)
        {
            return null;
        }

        if (!scan.OptionsAreObject)
        {
            return new BodyEdit(scan.OptionsStart, scan.OptionsEnd, OptionsBytes);
        }

        return scan.UsageStart >= 0
            ? new BodyEdit(scan.UsageStart, scan.UsageEnd, TrueBytes)
            : BodyEdit.Insert(scan.OptionsStart + 1, scan.OptionsEmpty ? UsageMemberBytes : [.. UsageMemberBytes, (byte)',']);
    }

    private static byte[] Apply(ReadOnlySpan<byte> body, IEnumerable<BodyEdit?> edits)
    {
        var result = body.ToArray();
        foreach (var edit in edits.OfType<BodyEdit>().OrderByDescending(static edit => edit.Start))
        {
            result = [.. result[..edit.Start], .. edit.Text, .. result[edit.End..]];
        }

        return result;
    }

    private static int ChoicesOf(JsonElement root)
    {
        if (!root.TryGetProperty(ChatApi.Fields.Choices, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return 1;
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var count) ? count : UnreadableChoices;
    }

    private static bool AllowsRetrieval(JsonElement choice) =>
        choice.ValueKind switch
        {
            JsonValueKind.String => choice.GetString() != NoToolChoice,
            JsonValueKind.Object => choice.TryGetProperty(ChatApi.Fields.Function, out var function) && function.ValueKind == JsonValueKind.Object
                                                                                                     && function.TryGetProperty(ChatApi.Fields.Name, out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == CcrRetrieval.Name,
            _ => true
        };

    private static BodyScan? Scan(ReadOnlySpan<byte> body)
    {
        try
        {
            return ScanBody(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BodyScan ScanBody(ReadOnlySpan<byte> body)
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

            if (reader.ValueTextEquals(ChatApi.Fields.Tools))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    continue;
                }

                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                scan.ToolsEnd = (int)reader.TokenStartIndex;
                (scan.ToolCount, scan.HasRetrieval) = ReadTools(body[start..(scan.ToolsEnd + 1)]);
            }
            else if (reader.ValueTextEquals(StreamOptions))
            {
                reader.Read();
                ScanOptions(ref reader, scan);
            }
        }

        return scan;
    }

    private static (int Count, bool HasRetrieval) ReadTools(ReadOnlySpan<byte> tools)
    {
        using var document = JsonDocument.Parse(tools.ToArray());
        return (document.RootElement.GetArrayLength(), document.RootElement.EnumerateArray().Any(static tool => tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty(ChatApi.Fields.Function, out var function)
            && function.ValueKind == JsonValueKind.Object && function.TryGetProperty(ChatApi.Fields.Name, out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == CcrRetrieval.Name));
    }

    private static void ScanOptions(ref Utf8JsonReader reader, BodyScan scan)
    {
        scan.HasStreamOptions = true;
        scan.UsageRequested = false;
        scan.UsageStart = -1;
        scan.UsageEnd = -1;
        scan.OptionsStart = (int)reader.TokenStartIndex;
        scan.OptionsAreObject = reader.TokenType == JsonTokenType.StartObject;
        if (!scan.OptionsAreObject)
        {
            reader.Skip();
            scan.OptionsEnd = (int)reader.BytesConsumed;
            return;
        }

        var depth = reader.CurrentDepth;
        scan.OptionsEmpty = true;
        while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth))
        {
            scan.OptionsEmpty = false;
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when reader.CurrentDepth == depth + 1 && reader.ValueTextEquals(UsageOption):
                    reader.Read();
                    scan.UsageStart = (int)reader.TokenStartIndex;
                    scan.UsageRequested = reader.TokenType == JsonTokenType.True;
                    reader.Skip();
                    scan.UsageEnd = (int)reader.BytesConsumed;
                    break;
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    reader.Skip();
                    break;
            }
        }

        scan.OptionsEnd = (int)reader.BytesConsumed;
    }

    /// <summary>One byte-range replacement in a request body; an insert when <paramref name="Start" /> equals <paramref name="End" />.</summary>
    /// <param name="Start">The first byte replaced.</param>
    /// <param name="End">The byte after the last one replaced.</param>
    /// <param name="Text">The bytes written in their place.</param>
    private readonly record struct BodyEdit(int Start, int End, byte[] Text)
    {
        public static BodyEdit Insert(int at, byte[] text) => new(at, at, text);
    }

    private sealed class BodyScan
    {
        public int ToolsEnd { get; set; } = -1;

        public int ObjectEnd { get; set; } = -1;

        public int ToolCount { get; set; }

        public bool HasRetrieval { get; set; }

        public bool HasStreamOptions { get; set; }

        public bool UsageRequested { get; set; }

        public bool OptionsAreObject { get; set; }

        public bool OptionsEmpty { get; set; }

        public int OptionsStart { get; set; } = -1;

        public int OptionsEnd { get; set; } = -1;

        public int UsageStart { get; set; } = -1;

        public int UsageEnd { get; set; } = -1;
    }
}
