using System.Text.Json;

namespace AiProxy.Core.Content;

/// <summary>The whole file each tool call of a request read, matched to its result by call id.</summary>
/// <remarks>
/// A path is kept only when it is certain: a read call with exactly one path argument and nothing
/// else, whose id no other call uses and exactly one result answers. Anything less is no path.
/// </remarks>
internal sealed class ToolCallFiles
{
    private static readonly string[] PathKeys = ["file_path", "path", "filePath"];

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
        if (id is null || _results.GetValueOrDefault(id) != 1)
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
        if (arguments is null)
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
        if (name is null || !name.Contains("read", StringComparison.OrdinalIgnoreCase) ||
            input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? path = null;
        var paths = 0;
        var allowLarge = false;
        foreach (var property in input.EnumerateObject())
        {
            if (PathKeys.Contains(property.Name, StringComparer.Ordinal) && property.Value.ValueKind == JsonValueKind.String)
            {
                paths++;
                path = property.Value.GetString();
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

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
