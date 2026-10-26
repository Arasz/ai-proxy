using System.Text.Json;
using AiProxy.Core.Json;

namespace AiProxy.Core.Content.Ccr;

/// <summary>Turns a <c>proxy_retrieve</c> call's JSON arguments into a request, or the reason it is malformed.</summary>
/// <param name="schema">The tool's input schema; it owns every rule a JSON Schema can state.</param>
public sealed class CcrRetrievalParser(IToolArgumentSchema schema)
{
    /// <summary>Returns the request, or null and a reason the model can act on.</summary>
    public CcrParseResult Parse(string arguments, CcrOptions options)
    {
        using var document = JsonDocument.TryParse(arguments);
        if (document is null) return CcrParseResult.Refused("The arguments are not JSON.");

        var root = document.RootElement;
        var violation = HasMalformedUnicode(root) ? "The arguments contain malformed Unicode." : schema.Violation(root);
        return violation is null ? Validate(root, options) : CcrParseResult.Refused(violation);
    }

    private static bool HasMalformedUnicode(JsonElement root)
    {
        if (!root.IsObject) return false;

        foreach (var property in root.EnumerateObject())
            if (!NameTranscodes(property) || (property.Value.IsString && property.Value.ReadString() is null))
                return true;

        return false;
    }

    // A name holding an unpaired surrogate escape throws when it is read.
    private static bool NameTranscodes(JsonProperty property)
    {
        try
        {
            _ = property.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static CcrParseResult Validate(JsonElement root, CcrOptions options)
    {
        var id = root.StringProperty(RetrievalJson.Id)!;
        var mode = root.StringProperty(RetrievalJson.Mode)!;
        var pattern = root.StringProperty(RetrievalJson.Pattern);
        var start = Integer(root, RetrievalJson.Start);
        var end = Integer(root, RetrievalJson.End);
        var context = Integer(root, RetrievalJson.Context);
        if (!start.Valid || !end.Valid || !context.Valid) return CcrParseResult.Refused("'start', 'end' and 'context' must be whole numbers in range.");

        if (context.Value > options.MaximumContext) return CcrParseResult.Refused(CcrText.Invariant($"'context' must be 0 to {options.MaximumContext}."));

        if (mode == RetrievalJson.LinesMode && (start.Value is null || end.Value is null)) return CcrParseResult.Refused("Mode lines needs 'start' and 'end'.");

        if (mode == RetrievalJson.PatternMode && pattern is null) return CcrParseResult.Refused("Mode pattern needs 'pattern'.");

        return CcrParseResult.Accepted(new CcrRetrievalRequest(id, mode, start.Value, end.Value, pattern, context.Value ?? 0));
    }

    private static IntegerArgument Integer(JsonElement root, string name)
    {
        var value = root.PropertyOrDefault(name);
        if (value.IsMissing) return IntegerArgument.Missing;

        return value.TryGetInt32(out var number) ? IntegerArgument.Of(number) : IntegerArgument.Invalid;
    }

    private sealed record IntegerArgument(int? Value, bool Valid)
    {
        public static IntegerArgument Missing { get; } = new(null, true);

        public static IntegerArgument Invalid { get; } = new(null, false);

        public static IntegerArgument Of(int value)
        {
            return new IntegerArgument(value, true);
        }
    }
}