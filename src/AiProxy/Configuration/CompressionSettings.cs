using AiProxy.Storage;

namespace AiProxy.Configuration;

/// <summary>What the operator configured about the compression stage.</summary>
/// <param name="Enabled">Whether the stage runs at all.</param>
/// <param name="Store">Where the store keeps what the proxy compressed.</param>
/// <param name="Minimum">The smallest part that is compressed, when the operator set one.</param>
public sealed record CompressionSettings(bool Enabled, ContentStoreOptions Store, int? Minimum)
{
    /// <summary>Reads the configuration, and leaves out whatever the operator did not set.</summary>
    /// <param name="configuration">The configuration to read.</param>
    public static CompressionSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = ConfigurationSwitch.IsOn(configuration["PROXY_COMPRESSION"]);

        var path = configuration["PROXY_COMPRESSION_DB"]?.Trim();
        var store = new ContentStoreOptions(
            string.IsNullOrWhiteSpace(path) ? ContentStoreOptions.DefaultDatabasePath : path);

        var minimum = int.TryParse(configuration["PROXY_COMPRESSION_MINIMUM"]?.Trim(), out var parsed)
            ? parsed
            : (int?)null;

        return new CompressionSettings(enabled, store, minimum);
    }
}
