using System.Text.Json;
using AiProxy.Core.Content.Ccr;
using Json.Schema;

namespace AiProxy.Ccr;

/// <summary>Checks <c>proxy_retrieve</c> arguments against the <c>input_schema</c> in the tool definition the model is sent.</summary>
public sealed class RetrievalArgumentSchema : IToolArgumentSchema
{
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    private static readonly string InputSchema = ReadInputSchema();

    private static readonly JsonSchema Schema = JsonSchema.FromText(InputSchema);

    /// <summary>The schema's JSON text, exactly as it appears in the tool definition.</summary>
    public string Text => InputSchema;

    /// <inheritdoc />
    public string? Violation(JsonElement arguments)
    {
        var results = Schema.Evaluate(arguments, Options);
        return results.IsValid ? null : Describe(results);
    }

    private static string ReadInputSchema()
    {
        using var definition = JsonDocument.Parse(CcrRetrieval.Definition);
        return definition.RootElement.GetProperty("input_schema").GetRawText();
    }

    private static string Describe(EvaluationResults results)
    {
        var failure = results.Details?.FirstOrDefault(static detail => detail.Errors is { Count: > 0 }) ?? results;
        var location = failure.InstanceLocation.ToString();
        var message = failure.Errors?.Values.FirstOrDefault() ?? "The arguments do not follow the tool's schema.";
        return location.Length == 0 ? message : $"{location}: {message}";
    }
}