using System.Text.Json;

namespace AiProxy.Core.Content;

/// <summary>The whole file each tool call of a request read, matched to its result by call id.</summary>
/// <remarks>
/// A path is kept only when it is certain: a read call with exactly one path argument and nothing
/// else, whose id no other call uses and exactly one result answers. Anything less is no path.
/// </remarks>
internal sealed class ToolCallFiles
{
    private static readonly HashSet<string> PathKeys = new(StringComparer.Ordinal) { "file_path", "path", "filePath" };

    private readonly Dictionary<string, FileRead?> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _results = new(StringComparer.Ordinal);

    /// <summary>Collects the calls and the result ids of the document.</summary>
    /// <param name="root">The parsed request body.</param>
    public static ToolCallFiles Collect(JsonElement root)
    {
        var files = new ToolCallFiles();
        files.Visit(root);
        return files;
    }

    /// <summary>The whole-file read the call answered by this result made, or null.</summary>
    /// <param name="result">The object that carries the tool output.</param>
    public FileRead? ReadFor(JsonElement result)
    {
        var id = ResultId(result);
        if (id is null || _results.GetValueOrDefault(id) != 1 ||
            (result.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True))
        {
            return null;
        }

        return _calls.GetValueOrDefault(id);
    }

    private void Visit(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Note(element);
                foreach (var property in element.EnumerateObject())
                {
                    Visit(property.Value);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item);
                }

                break;

            default:
                break;
        }
    }

    private void Note(JsonElement element)
    {
        var type = GetString(element, "type");

        if (type == "tool_use" && GetString(element, "id") is { } anthropicId)
        {
            var input = element.TryGetProperty("input", out var value) ? value : default;
            AddCall(anthropicId, PathOf(GetString(element, "name"), input));
        }
        else if (type == "function_call" && GetString(element, "call_id") is { } responsesId)
        {
            AddCall(responsesId, PathOf(GetString(element, "name"), GetString(element, "arguments")));
        }

        if (element.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object || GetString(call, "id") is not { } chatId)
                {
                    continue;
                }

                var function = call.TryGetProperty("function", out var value) && value.ValueKind == JsonValueKind.Object
                    ? value
                    : default;
                var path = function.ValueKind == JsonValueKind.Object
                    ? PathOf(GetString(function, "name"), GetString(function, "arguments"))
                    : null;
                AddCall(chatId, path);
            }
        }

        if (ResultId(element) is { } resultId)
        {
            _results[resultId] = _results.GetValueOrDefault(resultId) + 1;
        }
    }

    private void AddCall(string id, FileRead? path)
    {
        // A second call with the same id makes both ambiguous, so the id keeps no path.
        _calls[id] = _calls.ContainsKey(id) ? null : path;
    }

    private static string? ResultId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = GetString(element, "type");
        if (type == "tool_result")
        {
            return GetString(element, "tool_use_id");
        }

        if (type == "function_call_output")
        {
            return GetString(element, "call_id");
        }

        return GetString(element, "role") == "tool" ? GetString(element, "tool_call_id") : null;
    }

    private static FileRead? PathOf(string? name, string? arguments)
    {
        if (!IsReadName(name) || arguments is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            return PathOf(name, document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FileRead? PathOf(string? name, JsonElement input)
    {
        if (!IsReadName(name) || input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? path = null;
        var paths = 0;
        var allowLarge = false;
        foreach (var property in input.EnumerateObject())
        {
            if (PathKeys.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String)
            {
                paths++;
                path = ReadString(property.Value);
            }
            else if (property.NameEquals("allow_large") && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                allowLarge = property.Value.GetBoolean();
            }
            else
            {
                return null;
            }
        }

        return paths == 1 && !string.IsNullOrWhiteSpace(path) ? new FileRead(path, allowLarge) : null;
    }

    /// <summary>Whether one segment of the name is "read": Read, read_file, readFile, but not thread.</summary>
    private static bool IsReadName(string? name)
    {
        if (name is null)
        {
            return false;
        }

        var start = 0;
        for (var index = 1; index <= name.Length; index++)
        {
            var ends = index == name.Length ||
                       !char.IsLetterOrDigit(name[index]) ||
                       (char.IsUpper(name[index]) && char.IsLower(name[index - 1]));
            if (!ends)
            {
                continue;
            }

            if (name.AsSpan(start, index - start).Equals("read", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            start = index < name.Length && !char.IsLetterOrDigit(name[index]) ? index + 1 : index;
        }

        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? ReadString(value)
            : null;

    // A string that does not transcode, such as a lone surrogate, is a value this class cannot
    // trust, so it reads as absent and the call it belongs to keeps no path.
    private static string? ReadString(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
