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

        var indexes = FindToolOutputIndexes(body.Span);
        if (indexes.Count == 0)
        {
            return [];
        }

        return FindRegions(body.Span, indexes);
    }

    private static HashSet<int> FindToolOutputIndexes(ReadOnlySpan<byte> body)
    {
        var indexes = new HashSet<int>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return indexes;
        }

        using (document)
        {
            var counter = 0;
            Walk(document.RootElement, ref counter, indexes);
        }

        return indexes;
    }

    private static void Walk(JsonElement element, ref int counter, HashSet<int> indexes)
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
                        WalkToolOutput(property.Value, ref counter, indexes);
                        continue;
                    }

                    Walk(property.Value, ref counter, indexes);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, ref counter, indexes);
                }

                break;

            default:
                break;
        }
    }

    private static void WalkToolOutput(JsonElement value, ref int counter, HashSet<int> indexes)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                indexes.Add(counter);
                counter++;
                break;

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        Walk(item, ref counter, indexes);
                        continue;
                    }

                    foreach (var property in item.EnumerateObject())
                    {
                        if (property.NameEquals("text") && property.Value.ValueKind == JsonValueKind.String)
                        {
                            indexes.Add(counter);
                            counter++;
                            continue;
                        }

                        Walk(property.Value, ref counter, indexes);
                    }
                }

                break;

            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if ((property.NameEquals("output") || property.NameEquals("result")) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        indexes.Add(counter);
                        counter++;
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        WalkToolOutput(property.Value, ref counter, indexes);
                        continue;
                    }

                    Walk(property.Value, ref counter, indexes);
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

    private static List<ContentRegion> FindRegions(ReadOnlySpan<byte> body, HashSet<int> indexes)
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

            if (indexes.Contains(counter))
            {
                var start = (int)reader.TokenStartIndex;
                var end = EndOfStringToken(body, start);
                regions.Add(new ContentRegion(start, end - start, reader.GetString() ?? string.Empty));
            }

            counter++;
        }

        return regions;
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
