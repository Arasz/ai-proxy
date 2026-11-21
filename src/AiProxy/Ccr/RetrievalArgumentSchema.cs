using System.Text.Json;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Json;
using Json.Schema;

namespace AiProxy.Ccr;

/// <summary>Checks <c>proxy_retrieve</c> arguments against the <c>input_schema</c> in the tool definition the model is sent.</summary>
public sealed class RetrievalArgumentSchema : IToolArgumentSchema
{
    /// <summary>The schema's JSON text, exactly as it appears in the tool definition.</summary>
    public static string Text { get; } = CcrRetrieval.Definition.ReadRawProperty("input_schema");

    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    private static readonly JsonElement Properties = JsonDocument.Parse(Text).RootElement.GetProperty("properties");

    private static readonly JsonSchema Schema = JsonSchema.FromText(Text);

    /// <inheritdoc />
    public string? Violation(JsonElement arguments)
    {
        var results = Schema.Evaluate(arguments, Options);
        return results.IsValid ? null : Describe(results);
    }

    private static string Describe(EvaluationResults results)
    {
        var failing = results.Details?.Where(static detail => detail.Errors is { Count: > 0 }).ToArray() ?? [];
        var failure = failing.FirstOrDefault(static detail => detail.InstanceLocation.ToString().Length > 0) ?? failing.FirstOrDefault() ?? results;
        var location = failure.InstanceLocation.ToString();
        var message = failure.Errors?.Values.FirstOrDefault() ?? "The arguments do not follow the tool's schema.";
        var allowed = AllowedValues(location);
        return location.Length == 0 ? message : $"{location}: {message}{allowed}";
    }

    private static string AllowedValues(string location)
    {
        var name = location.TrimStart('/');
        if (!Properties.TryGetProperty(name, out var property) || !property.TryGetProperty("enum", out var values))
        {
            return "";
        }

        return $" Allowed values: {string.Join(", ", values.EnumerateArray().Select(static value => value.GetString()))}.";
    }
}
