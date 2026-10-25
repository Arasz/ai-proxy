using System.Text.Json;

namespace AiProxy.Core.Content;

/// <summary>Finds the output of a tool in a request body, and nothing else.</summary>
/// <remarks>
/// The body is read twice. The first pass walks the parsed document and counts the string
/// values in the order they appear, and notes which of them carry the output of a tool. The
/// second pass walks the raw bytes and counts the string tokens in the same order, so a
/// region is found by its position rather than by its content. That is what keeps a message
/// the client wrote, which happens to repeat the tool's words, out of the regions.
/// </remarks>
public sealed class ToolOutputRegionFinder
{
    /// <summary>Finds the tool output in the body.</summary>
    /// <param name="body">The request body.</param>
    public IReadOnlyList<ContentRegion> Find(ReadOnlyMemory<byte> body)
    {
        if (body.Length == 0)
        {
            return [];
        }

        var (indexes, documentCount) = FindToolOutputIndexes(body.Span);
        if (indexes.Count == 0)
        {
            return [];
        }

        var (regions, tokenCount) = FindRegions(body.Span, indexes);

        // The two passes count the string values of the body in the same order, with the
        // same parser, so the counts agree unless one of them stopped early. A reader
        // that saw MORE strings than the document has would mean the two disagree about
        // the order, and a region found by position would then be the wrong region.
        return tokenCount > documentCount ? [] : regions;
    }

    private static (Dictionary<int, FileRead?> Indexes, int Count) FindToolOutputIndexes(ReadOnlySpan<byte> body)
    {
        var indexes = new Dictionary<int, FileRead?>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return (indexes, 0);
        }

        using (document)
        {
            var counter = 0;
            var files = ToolCallFiles.Collect(document.RootElement);
            Walk(document.RootElement, ref counter, indexes, files);
            return (indexes, counter);
        }
    }

    private static void Walk(JsonElement element, ref int counter, Dictionary<int, FileRead?> indexes, ToolCallFiles files)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                counter++;
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsToolOutputProperty(element, property))
                    {
                        WalkToolOutput(property.Value, ref counter, indexes, files, files.ReadFor(element));
                        continue;
                    }

                    Walk(property.Value, ref counter, indexes, files);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, ref counter, indexes, files);
                }

                break;

            default:
                break;
        }
    }

    private static void WalkToolOutput(
        JsonElement value,
        ref int counter,
        Dictionary<int, FileRead?> indexes,
        ToolCallFiles files,
        FileRead? path)
    {
        // A path names one whole file, so an output that spans more than one string keeps none.
        var first = counter;
        var before = indexes.Count;
        WalkToolOutputStrings(value, ref counter, indexes, files, path);
        if (path is null || indexes.Count - before <= 1)
        {
            return;
        }

        for (var index = first; index < counter; index++)
        {
            if (indexes.ContainsKey(index))
            {
                indexes[index] = null;
            }
        }
    }

    private static void WalkToolOutputStrings(
        JsonElement value,
        ref int counter,
        Dictionary<int, FileRead?> indexes,
        ToolCallFiles files,
        FileRead? path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                indexes[counter] = path;
                counter++;
                break;

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        Walk(item, ref counter, indexes, files);
                        continue;
                    }

                    foreach (var property in item.EnumerateObject())
                    {
                        if (property.NameEquals("text") && property.Value.ValueKind == JsonValueKind.String)
                        {
                            indexes[counter] = path;
                            counter++;
                            continue;
                        }

                        Walk(property.Value, ref counter, indexes, files);
                    }
                }

                break;

            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if ((property.NameEquals("output") || property.NameEquals("result")) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        indexes[counter] = path;
                        counter++;
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        WalkToolOutputStrings(property.Value, ref counter, indexes, files, path);
                        continue;
                    }

                    Walk(property.Value, ref counter, indexes, files);
                }

                break;

            default:
                break;
        }
    }

    private static bool IsToolOutputProperty(JsonElement parent, JsonProperty property)
    {
        var type = GetString(parent, "type");
        var role = GetString(parent, "role");

        if ((type == "tool_result" || role == "tool") && property.NameEquals("content"))
        {
            return property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Array;
        }

        if (type == "function_call_output" && property.NameEquals("output"))
        {
            return property.Value.ValueKind == JsonValueKind.String;
        }

        return property.NameEquals("functionResponse") && property.Value.ValueKind == JsonValueKind.Object;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static (List<ContentRegion> Regions, int Count) FindRegions(ReadOnlySpan<byte> body, Dictionary<int, FileRead?> indexes)
    {
        var regions = new List<ContentRegion>();
        var reader = new Utf8JsonReader(body);
        var counter = 0;

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                continue;
            }

            if (indexes.TryGetValue(counter, out var read))
            {
                var start = (int)reader.TokenStartIndex;
                var end = EndOfStringToken(body, start);
                var text = reader.GetString() ?? "";
                regions.Add(new ContentRegion(start, end - start, text, read?.PathFor(text)));
            }

            counter++;
        }

        return (regions, counter);
    }

    private static int EndOfStringToken(ReadOnlySpan<byte> body, int start)
    {
        var index = start + 1;

        while (index < body.Length)
        {
            var current = body[index];

            if (current == (byte)'\\')
            {
                index += 2;
                continue;
            }

            if (current == (byte)'"')
            {
                return index + 1;
            }

            index++;
        }

        return body.Length;
    }
}
