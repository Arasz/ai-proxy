using System.Text.Json;
using AiProxy.Core.Json;

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

        var found = FindToolOutputIndexes(body.Span);
        if (found.Indexes.Count == 0)
        {
            return [];
        }

        var scan = FindRegions(body.Span, found.Indexes);

        // The two passes count the string values of the body in the same order, with the
        // same parser, so the counts agree unless one of them stopped early. A reader
        // that saw MORE strings than the document has would mean the two disagree about
        // the order, and a region found by position would then be the wrong region.
        return scan.Count > found.Count ? [] : scan.Regions;
    }

    private static ToolOutputIndexes FindToolOutputIndexes(ReadOnlySpan<byte> body)
    {
        var indexes = new Dictionary<int, FileRead?>();

        using var document = JsonDocument.TryParse(body.ToArray());
        if (document is null)
        {
            return new ToolOutputIndexes(indexes, 0);
        }

        var files = ToolCallFiles.Collect(document.RootElement);
        var count = Walk(document.RootElement, 0, indexes, files);
        return new ToolOutputIndexes(indexes, count);
    }

    /// <summary>Walks the element and returns the counter after its last string.</summary>
    private static int Walk(JsonElement element, int counter, Dictionary<int, FileRead?> indexes, ToolCallFiles files)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return counter + 1;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    counter = IsToolOutputProperty(element, property)
                        ? WalkToolOutput(property.Value, counter, indexes, files, files.ReadFor(element))
                        : Walk(property.Value, counter, indexes, files);
                }

                return counter;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    counter = Walk(item, counter, indexes, files);
                }

                return counter;

            default:
                return counter;
        }
    }

    private static int WalkToolOutput(
        JsonElement value,
        int counter,
        Dictionary<int, FileRead?> indexes,
        ToolCallFiles files,
        FileRead? path)
    {
        // A path names one whole file, so an output that spans more than one string keeps none.
        var before = indexes.Count;
        var next = WalkToolOutputStrings(value, counter, indexes, files, path);
        if (path is null || indexes.Count - before <= 1)
        {
            return next;
        }

        for (var index = counter; index < next; index++)
        {
            if (indexes.ContainsKey(index))
            {
                indexes[index] = null;
            }
        }

        return next;
    }

    private static int WalkToolOutputStrings(
        JsonElement value,
        int counter,
        Dictionary<int, FileRead?> indexes,
        ToolCallFiles files,
        FileRead? path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return Record(counter, indexes, path);

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    counter = WalkContentBlock(item, counter, indexes, files, path);
                }

                return counter;

            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    counter = WalkResultProperty(property, counter, indexes, files, path);
                }

                return counter;

            default:
                return counter;
        }
    }

    /// <summary>A block of an output array keeps its <c>text</c> string as output; everything else is walked as ordinary body.</summary>
    private static int WalkContentBlock(
        JsonElement block,
        int counter,
        Dictionary<int, FileRead?> indexes,
        ToolCallFiles files,
        FileRead? path)
    {
        if (!block.IsObject)
        {
            return Walk(block, counter, indexes, files);
        }

        foreach (var property in block.EnumerateObject())
        {
            counter = property.IsString(ToolCallJson.Text)
                ? Record(counter, indexes, path)
                : Walk(property.Value, counter, indexes, files);
        }

        return counter;
    }

    /// <summary>A property of an output object keeps its <c>output</c> or <c>result</c> string as output, and looks inside nested objects.</summary>
    private static int WalkResultProperty(
        JsonProperty property,
        int counter,
        Dictionary<int, FileRead?> indexes,
        ToolCallFiles files,
        FileRead? path)
    {
        if (property.IsString(ToolCallJson.Output) || property.IsString(ToolCallJson.Result))
        {
            return Record(counter, indexes, path);
        }

        return property.Value.IsObject
            ? WalkToolOutputStrings(property.Value, counter, indexes, files, path)
            : Walk(property.Value, counter, indexes, files);
    }

    private static int Record(int counter, Dictionary<int, FileRead?> indexes, FileRead? path)
    {
        indexes[counter] = path;
        return counter + 1;
    }

    private static bool IsToolOutputProperty(JsonElement parent, JsonProperty property)
    {
        var type = parent.StringProperty(ToolCallJson.Type);
        var role = parent.StringProperty(ToolCallJson.Role);

        return (type, role) switch
        {
            (ToolCallJson.ToolResult, _) or (_, ToolCallJson.Tool) when property.NameEquals(ToolCallJson.Content) => property.Value.IsString || property.Value.IsArray,
            (ToolCallJson.FunctionCallOutput, _) when property.NameEquals(ToolCallJson.Output) => property.Value.IsString,
            _ => property.NameEquals(ToolCallJson.FunctionResponse) && property.Value.IsObject
        };
    }

    private static ToolOutputRegions FindRegions(ReadOnlySpan<byte> body, Dictionary<int, FileRead?> indexes)
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
                var end = body.EndOfStringToken(start);
                var text = reader.GetString() ?? "";
                regions.Add(new ContentRegion(start, end - start, text, read?.PathFor(text)));
            }

            counter++;
        }

        return new ToolOutputRegions(regions, counter);
    }
}

file static class EndOfStringTokenExtensions
{
    private const byte A = (byte)'\\';
    private const byte B = (byte)'"';

    extension(ReadOnlySpan<byte> body)
    {
        internal int EndOfStringToken(int start)
        {
            var index = start + 1;

            while (index < body.Length)
            {
                var current = body[index];

                switch (current)
                {
                    case A:
                        index += 2;
                        continue;
                    case B:
                        return index + 1;
                    default:
                        index++;
                        break;
                }
            }

            return body.Length;
        }
    }
}
