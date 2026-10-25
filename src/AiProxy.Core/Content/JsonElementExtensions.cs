using System.Text.Json;

namespace AiProxy.Core.Content;

/// <summary>Reads of a parsed <see cref="JsonElement" /> that tolerate a missing or mistyped value.</summary>
internal static class JsonElementExtensions
{
    /// <summary>The property of the object, or an undefined element when there is none.</summary>
    /// <param name="element">The element to read from.</param>
    /// <param name="name">The property name.</param>
    public static JsonElement PropertyOrDefault(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    /// <summary>The string value of the property, or null when it is missing, not a string, or does not transcode.</summary>
    /// <param name="element">The element to read from.</param>
    /// <param name="name">The property name.</param>
    public static string? StringProperty(this JsonElement element, string name) =>
        element.PropertyOrDefault(name).ReadString();

    /// <summary>Whether the property is the JSON value true.</summary>
    /// <param name="element">The element to read from.</param>
    /// <param name="name">The property name.</param>
    public static bool IsTrue(this JsonElement element, string name) =>
        element.PropertyOrDefault(name).ValueKind == JsonValueKind.True;

    /// <summary>The string the element holds, or null when it is not a string or does not transcode.</summary>
    /// <remarks>A string such as a lone surrogate does not transcode, so it reads as absent.</remarks>
    /// <param name="value">The element to read.</param>
    public static string? ReadString(this JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

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
