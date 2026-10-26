using System.Text.Json;
using AiProxy.Code;
using AiProxy.Core.Content.Code;

namespace CodeReducerCore;

/// <summary>Runs the offline JSON adapter for a caller-supplied code-read view.</summary>
public static class CodeReducerCommand
{
    /// <summary>Reads one request and writes a reduced view, or a diagnostic with a nonzero result.</summary>
    public static async Task<int> RunAsync(TextReader input, TextWriter output, TextWriter error)
    {
        try
        {
            using var document = JsonDocument.Parse(await input.ReadToEndAsync().ConfigureAwait(false));
            var root = document.RootElement;
            Validate(root);
            var text = root.GetProperty("text").GetString()!;
            var path = root.TryGetProperty("filePath", out var filePath) ? filePath.GetString() : null;
            var hint = root.TryGetProperty("retrievalHint", out var retrievalHint) ? retrievalHint.GetString() : null;
            var options = new CodeReductionOptions(
                !root.TryGetProperty("removeComments", out var removeComments) || removeComments.GetBoolean(),
                !root.TryGetProperty("removeImports", out var removeImports) || removeImports.GetBoolean(),
                root.TryGetProperty("renameIdentifiers", out var renameIdentifiers) && renameIdentifiers.GetBoolean());
            var result = new CodeReducer(new CSharpCodeLexer()).Reduce(text, path, options, hint);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { text = result.Text, changed = result.Changed, supported = result.Supported })).ConfigureAwait(false);
            return 0;
        }
        catch (JsonException)
        {
            await error.WriteLineAsync("Invalid reducer request JSON.").ConfigureAwait(false);
            return 1;
        }
    }

    private static void Validate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException();
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new JsonException();
            }

            var valid = property.Name switch
            {
                "text" => property.Value.ValueKind == JsonValueKind.String,
                "filePath" or "retrievalHint" => property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                "removeComments" or "removeImports" or "renameIdentifiers" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => false
            };
            if (!valid)
            {
                throw new JsonException();
            }
        }

        if (!names.Contains("text"))
        {
            throw new JsonException();
        }
    }
}