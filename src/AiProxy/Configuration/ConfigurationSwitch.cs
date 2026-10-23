namespace AiProxy.Configuration;

/// <summary>Reads an on/off setting: <c>on</c> or <c>true</c>, in any case, is on; anything else is off.</summary>
public static class ConfigurationSwitch
{
    /// <summary>Whether the value turns the setting on.</summary>
    public static bool IsOn(string? value) =>
        value?.Trim() is { } trimmed &&
        (trimmed.Equals("on", StringComparison.OrdinalIgnoreCase) ||
         trimmed.Equals("true", StringComparison.OrdinalIgnoreCase));
}
