using System.Text.Json;

namespace AiProxy.Core.Json;

/// <summary>Reads of a parsed <see cref="JsonElement" /> that tolerate a missing or mistyped value.</summary>
internal static class JsonElementExtensions
{
    /// <param name="property">The property to test.</param>
    extension(JsonProperty property)
    {
        /// <summary>Whether the property has this name and holds a string.</summary>
        /// <param name="name">The property name.</param>
        public bool IsString(string name) =>
            property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String;
    }

    /// <param name="element">The element to read from.</param>
    extension(JsonElement element)
    {
        /// <summary>The property of the object, or an undefined element when there is none.</summary>
        /// <param name="name">The property name.</param>
        public JsonElement PropertyOrDefault(string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

        /// <summary>The string value of the property, or null when it is missing, not a string, or does not transcode.</summary>
        /// <param name="name">The property name.</param>
        public string? StringProperty(string name) =>
            element.PropertyOrDefault(name).ReadString();

        /// <summary>Whether the property is the JSON value true.</summary>
        /// <param name="name">The property name.</param>
        public bool IsTrue(string name) =>
            element.PropertyOrDefault(name).ValueKind == JsonValueKind.True;

        /// <summary>The integer value of the property, or null when it is missing, not a number, or not an integer that fits.</summary>
        /// <param name="name">The property name.</param>
        public long? Int64Property(string name)
        {
            var value = element.PropertyOrDefault(name);
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) ? count : null;
        }

        /// <summary>The string the element holds, or null when it is not a string or does not transcode.</summary>
        /// <remarks>A string such as a lone surrogate does not transcode, so it reads as absent.</remarks>
        public string? ReadString()
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            try
            {
                return element.GetString();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
