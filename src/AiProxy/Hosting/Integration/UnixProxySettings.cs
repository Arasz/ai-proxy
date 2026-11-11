using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Hosting.Integration;

/// <summary>
///     Compares macOS and Linux proxy snapshots value by value (every proxy, flag and bypass list the backend restores),
///     so a restore can tell settings it left behind from settings someone else changed.
/// </summary>
internal static class UnixProxySettings
{
    /// <summary>The capturing process's own environment and the backend's armed flag are not system settings.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { "_originalEnv", "_hasSnapshot" };

    /// <summary>True when every value is either ai-proxy's or the original's, and at least one changed value is still ai-proxy's.</summary>
    public static bool IsOwned(string current, string applied, string original)
    {
        var currentValues = Values(current);
        var appliedValues = Values(applied);
        var originalValues = Values(original);
        var keys = currentValues.Keys.Union(appliedValues.Keys).Union(originalValues.Keys);
        var anyStillApplied = false;
        foreach (var key in keys)
        {
            var value = currentValues.GetValueOrDefault(key);
            var appliedValue = appliedValues.GetValueOrDefault(key);
            var originalValue = originalValues.GetValueOrDefault(key);
            if (value == appliedValue)
            {
                anyStillApplied |= appliedValue != originalValue;
            }
            else if (value != originalValue)
            {
                return false;
            }
        }

        return anyStillApplied;
    }

    /// <summary>True when the applied settings differ from the original ones in at least one compared value.</summary>
    public static bool Differ(string applied, string original)
    {
        var appliedValues = Values(applied);
        var originalValues = Values(original);
        return appliedValues.Count != originalValues.Count ||
               appliedValues.Any(pair => originalValues.GetValueOrDefault(pair.Key) != pair.Value);
    }

    private static Dictionary<string, string> Values(string snapshot)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(snapshot);
        foreach (var field in document.RootElement.GetProperty("values").EnumerateObject())
        {
            if (!Ignored.Contains(field.Name))
            {
                Flatten(field.Name, JsonNode.Parse(field.Value.GetString() ?? "null"), values);
            }
        }

        return values;
    }

    private static void Flatten(string path, JsonNode? node, Dictionary<string, string> values)
    {
        switch (node)
        {
            case JsonObject item when item.Count > 0:
                foreach (var (name, child) in item)
                {
                    Flatten($"{path}.{name}", child, values);
                }

                break;
            case JsonArray items when items.Count > 0:
                for (var index = 0; index < items.Count; index++)
                {
                    Flatten($"{path}[{index}]", items[index], values);
                }

                break;
            default:
                values[path] = node?.ToJsonString() ?? "null";
                break;
        }
    }
}
