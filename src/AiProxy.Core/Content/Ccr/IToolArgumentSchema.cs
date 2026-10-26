using System.Text.Json;

namespace AiProxy.Core.Content.Ccr;

/// <summary>Checks a tool call's arguments against the tool's input schema.</summary>
public interface IToolArgumentSchema
{
    /// <summary>Returns why the arguments break the schema, or null when they follow it.</summary>
    string? Violation(JsonElement arguments);
}