using System.Text.Json;

namespace AiProxy.Core.Content.Ccr;

/// <summary>Turns a <c>proxy_retrieve</c> call's JSON arguments into a request, or the reason it is malformed.</summary>
public sealed class CcrRetrievalParser
{
    private static readonly string[] Properties = ["id", "mode", "start", "end", "pattern", "context"];

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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new(null, "The arguments are not a JSON object.");
            }

            var unknown = CheckProperties(root);
            return unknown is null ? Validate(root, options) : new(null, unknown);
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

    private static string? CheckProperties(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            string name;
            try
            {
                name = property.Name;
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    _ = property.Value.GetString();
                }
            }
            catch (InvalidOperationException)
            {
                return "The arguments contain malformed Unicode.";
            }

            if (!Properties.Contains(name, StringComparer.Ordinal))
            {
                return $"Unknown property '{name}'.";
            }
        }

        return null;
    }

    private static CcrParseResult Validate(JsonElement root, CcrOptions options)
    {
        var id = String(root, "id");
        if (id is null || !IsId(id))
        {
            return new(null, "'id' must be the 16 lowercase hex characters from a [proxy preview ...] line.");
        }

        var mode = String(root, "mode");
        if (mode is not ("lines" or "pattern" or "whole"))
        {
            return new(null, "'mode' must be lines, pattern or whole.");
        }

        var start = Integer(root, "start");
        var end = Integer(root, "end");
        var context = Integer(root, "context");
        var pattern = String(root, "pattern");
        if (!start.Valid || !end.Valid || !context.Valid || (root.TryGetProperty("pattern", out _) && pattern is null))
        {
            return new(null, "'start', 'end' and 'context' must be integers and 'pattern' a string.");
        }

        if (context.Value is < 0 || context.Value > options.MaximumContext)
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

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IntegerArgument Integer(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return new(null, true);
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? new(number, true) : new(null, false);
    }

    private static bool IsId(string id) => id.Length == 16 && id.All(char.IsAsciiHexDigitLower);

    private sealed record ParsedDocument(JsonDocument? Document, string Reason);

    private sealed record IntegerArgument(int? Value, bool Valid);
}
