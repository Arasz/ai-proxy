using AiProxy.Storage;

namespace AiProxy.Configuration;

/// <summary>What the operator configured about the compression stage, bound from <c>Proxy:Compression</c>.</summary>
public sealed record CompressionSettings
{
    /// <summary>Whether the stage runs at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Where the store keeps what the proxy compressed; blank means <see cref="ContentStoreOptions.DefaultDatabasePath"/>.</summary>
    public string? Database { get; init; }

    /// <summary>The smallest part that is compressed, when the operator set one.</summary>
    public int? Minimum { get; init; }

    /// <summary>The store options the <see cref="Database"/> setting resolves to.</summary>
    public ContentStoreOptions Store => new(
        string.IsNullOrWhiteSpace(Database) ? ContentStoreOptions.DefaultDatabasePath : Database.Trim());
}
