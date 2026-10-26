using System.Text.Json;

namespace AiProxy.Core.Json;

/// <summary>Reads of a parsed <see cref="JsonElement" /> that tolerate a missing or mistyped value.</summary>
internal static class JsonExtensions
{
    extension(string text)
    {
        public bool IsJson()
        {
            using var document = JsonDocument.TryParse(text);
            return document is not null;
        }
    }

    extension(JsonDocument)
    {
        /// <summary>Parses the text or returns null when it is not JSON or holds an unpaired surrogate.</summary>
        /// <param name="text">The text to parse.</param>
        public static JsonDocument? TryParse(string text)
        {
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Parses the bytes or returns null when they are not JSON.</summary>
        /// <param name="bytes">The UTF-8 bytes to parse.</param>
        public static JsonDocument? TryParse(ReadOnlyMemory<byte> bytes)
        {
            try
            {
                return JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <param name="property">The property to test.</param>
    extension(JsonProperty property)
    {
        /// <summary>Whether the property has this name and holds a string.</summary>
        /// <param name="name">The property name.</param>
        public bool IsString(string name)
        {
            return property.NameEquals(name) && property.Value.IsString;
        }
    }

    /// <param name="element">The element to read from.</param>
    extension(JsonElement element)
    {
        /// <summary>Whether the element is a JSON object.</summary>
        public bool IsObject => element.ValueKind == JsonValueKind.Object;

        /// <summary>Whether the element is a JSON array.</summary>
        public bool IsArray => element.ValueKind == JsonValueKind.Array;

        /// <summary>Whether the element is a JSON string.</summary>
        public bool IsString => element.ValueKind == JsonValueKind.String;

        /// <summary>Whether the element is the JSON value true or false.</summary>
        public bool IsBoolean => element.ValueKind is JsonValueKind.True or JsonValueKind.False;

        /// <summary>Whether the element is the undefined one a missing property reads as.</summary>
        public bool IsMissing => element.ValueKind == JsonValueKind.Undefined;

        /// <summary>The values of an object's properties or the items of an array; nothing for any other element.</summary>
        public IEnumerable<JsonElement> Children()
        {
            if (element.IsObject)
                foreach (var property in element.EnumerateObject())
                    yield return property.Value;
            else if (element.IsArray)
                foreach (var item in element.EnumerateArray())
                    yield return item;
        }

        /// <summary>The property of the object, or an undefined element when there is none.</summary>
        /// <param name="name">The property name.</param>
        public JsonElement PropertyOrDefault(string name)
        {
            return element.IsObject && element.TryGetProperty(name, out var value) ? value : default;
        }

        /// <summary>The string value of the property, or null when it is missing, not a string, or does not transcode.</summary>
        /// <param name="name">The property name.</param>
        public string? StringProperty(string name)
        {
            return element.PropertyOrDefault(name).ReadString();
        }

        /// <summary>Whether the property is the JSON value true.</summary>
        /// <param name="name">The property name.</param>
        public bool IsTrue(string name)
        {
            return element.PropertyOrDefault(name).ValueKind == JsonValueKind.True;
        }

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
            if (!element.IsString) return null;

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