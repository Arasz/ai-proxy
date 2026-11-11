using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Hosting.Integration;

/// <summary>Converts Titanium's private snapshot values to JSON and back by reflection.</summary>
internal static class SystemProxySnapshotCodec
{
    /// <summary>Serializes a value of <paramref name="type" /> to JSON text.</summary>
    public static string Write(object? value, Type type)
    {
        return Encode(value, type)?.ToJsonString() ?? "null";
    }

    /// <summary>Rebuilds a value of <paramref name="type" /> from JSON text written by <see cref="Write" />.</summary>
    public static object? Read(string json, Type type)
    {
        return Decode(JsonNode.Parse(json), type);
    }

    /// <summary>Encodes scalars, enums, sequences, dictionaries and records as JSON nodes.</summary>
    private static JsonNode? Encode(object? value, Type type)
    {
        if (value is null)
        {
            return null;
        }

        var actualType = value.GetType();
        if (type.IsEnum)
        {
            return JsonValue.Create(value.ToString());
        }

        if (IsScalar(type) || type == typeof(byte[]))
        {
            return JsonSerializer.SerializeToNode(value, type);
        }

        if (value is IDictionary dictionary)
        {
            var valueType = type.GetGenericArguments()[1];
            var result = new JsonObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                result[entry.Key?.ToString() ?? ""] = Encode(entry.Value, valueType);
            }

            return result;
        }

        if (value is IEnumerable sequence)
        {
            var elementType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            var result = new JsonArray();
            foreach (var item in sequence)
            {
                result.Add(Encode(item, elementType));
            }

            return result;
        }

        var objectNode = new JsonObject();
        foreach (var property in Properties(actualType))
        {
            objectNode[property.Name] = Encode(property.GetValue(value), property.PropertyType);
        }

        return objectNode;
    }

    /// <summary>Decodes a JSON node into <paramref name="type" />, calling its widest constructor for records.</summary>
    private static object? Decode(JsonNode? node, Type type)
    {
        if (node is null)
        {
            return null;
        }

        var nullableType = Nullable.GetUnderlyingType(type);
        if (nullableType is not null)
        {
            return Decode(node, nullableType);
        }

        if (type.IsEnum)
        {
            return Enum.Parse(type, node.GetValue<string>(), false);
        }

        if (IsScalar(type) || type == typeof(byte[]))
        {
            return node.Deserialize(type);
        }

        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            var values = node.AsArray().Select(item => Decode(item, elementType)).ToArray();
            var array = Array.CreateInstance(elementType, values.Length);
            for (var index = 0; index < values.Length; index++)
            {
                array.SetValue(values[index], index);
            }

            return array;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var arguments = type.GetGenericArguments();
            var keyType = arguments[0];
            var valueType = arguments[1];
            var dictionary = (IDictionary)Activator.CreateInstance(type)!;
            foreach (var pair in node.AsObject())
            {
                var key = keyType.IsEnum ? Enum.Parse(keyType, pair.Key, false) : Convert.ChangeType(pair.Key, keyType);
                dictionary.Add(key!, Decode(pair.Value, valueType));
            }

            return dictionary;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = type.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(type)!;
            foreach (var item in node.AsArray())
            {
                list.Add(Decode(item, elementType));
            }

            return list;
        }

        var properties = Properties(type).ToArray();
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(item => item.GetParameters().Length)
            .FirstOrDefault();
        if (constructor is null)
        {
            throw new InvalidOperationException($"No constructor is available for proxy snapshot type {type.FullName}.");
        }

        var constructorParameters = constructor.GetParameters();
        var argumentsForConstructor = constructorParameters.Select(parameter =>
        {
            var property = properties.SingleOrDefault(candidate =>
                candidate.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (property is null)
            {
                throw new InvalidOperationException(
                    $"Proxy snapshot constructor parameter {parameter.Name} has no matching property on {type.FullName}.");
            }

            return Decode(node[property.Name], parameter.ParameterType);
        }).ToArray();
        var instance = constructor.Invoke(argumentsForConstructor);

        foreach (var property in properties.Where(item => item.SetMethod is not null))
        {
            property.SetValue(instance, Decode(node[property.Name], property.PropertyType));
        }

        return instance;
    }

    /// <summary>The readable, non-indexed instance properties that make up a value.</summary>
    private static IEnumerable<PropertyInfo> Properties(Type type)
    {
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => property.Name != "EqualityContract" && property.GetIndexParameters().Length == 0 && property.GetMethod is not null);
    }

    /// <summary>True for primitives, value types and strings, which serialize directly.</summary>
    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsValueType || type == typeof(string) || type == typeof(decimal);
    }
}
