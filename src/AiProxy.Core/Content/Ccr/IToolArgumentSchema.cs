using System.Text.Json;

namespace AiProxy.Core.Content.Ccr;

/// <summary>Checks a tool call's arguments against the tool's input schema.</summary>
public interface IToolArgumentSchema
{
    /// <summary>Returns <see cref="Valid"/> when the arguments follow the schema, else <see cref="Invalid"/> with why not.</summary>
    Validation Check(JsonElement arguments);
}