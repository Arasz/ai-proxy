using System.Text.Json;

namespace AiProxy.Core.Content.Ccr;

/// <summary>Turns a <c>proxy_retrieve</c> call's JSON arguments into a request, or the reason it is malformed.</summary>
/// <param name="schema">The tool's input schema; it owns every rule a JSON Schema can state.</param>
public sealed class CcrRetrievalParser(IToolArgumentSchema schema)
{
    /// <summary>Returns the request, or null and a reason the model can act on.</summary>
    public CcrParseResult Parse(string arguments, CcrOptions options)
    {
        var read = Read(arguments);
        if (read.Document is not { } document)
        {
            return new(null, read.Reason);
        }

        using (document)
        {
            var root = document.RootElement;
            var violation = HasMalformedUnicode(root) ? "The arguments contain malformed Unicode." : schema.Violation(root);
            return violation is null ? Validate(root, options) : new(null, violation);
        }
    }

    private static ParsedDocument Read(string arguments)
    {
        try
        {
            return new(JsonDocument.Parse(arguments), "");
        }
        catch (JsonException)
        {
            return new(null, "The arguments are not JSON.");
        }
        catch (ArgumentException)
        {
            return new(null, "The arguments contain malformed Unicode.");
        }
    }

    private static bool HasMalformedUnicode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            foreach (var property in root.EnumerateObject())
            {
                _ = property.Name;
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    _ = property.Value.GetString();
                }
            }

            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static CcrParseResult Validate(JsonElement root, CcrOptions options)
    {
        var id = root.GetProperty("id").GetString()!;
        var mode = root.GetProperty("mode").GetString()!;
        var start = Integer(root, "start");
        var end = Integer(root, "end");
        var context = Integer(root, "context");
        var pattern = root.TryGetProperty("pattern", out var value) ? value.GetString() : null;
        if (!start.Valid || !end.Valid || !context.Valid)
        {
            return new(null, "'start', 'end' and 'context' must be whole numbers in range.");
        }

        if (context.Value > options.MaximumContext)
        {
            return new(null, CcrText.Invariant($"'context' must be 0 to {options.MaximumContext}."));
        }

        if (mode == "lines" && (start.Value is null || end.Value is null))
        {
            return new(null, "Mode lines needs 'start' and 'end'.");
        }

        if (mode == "pattern" && pattern is null)
        {
            return new(null, "Mode pattern needs 'pattern'.");
        }

        return new(new CcrRetrievalRequest(id, mode, start.Value, end.Value, pattern, context.Value ?? 0), "");
    }

    private static IntegerArgument Integer(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return new(null, true);
        }

        return value.TryGetInt32(out var number) ? new(number, true) : new(null, false);
    }

    private sealed record ParsedDocument(JsonDocument? Document, string Reason);

    private sealed record IntegerArgument(int? Value, bool Valid);
}
