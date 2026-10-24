namespace AiProxy.Storage;

/// <summary>Where the store keeps what the proxy compressed.</summary>
/// <param name="DatabasePath">The path of the SQLite file.</param>
public sealed record ContentStoreOptions(string DatabasePath)
{
    /// <summary>The path the store uses when the operator sets none.</summary>
    public const string DefaultDatabasePath = "~/.ai-proxy/content.db";

    /// <summary>The path with the home directory spelled out.</summary>
    public string ResolvedDatabasePath =>
        DatabasePath.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                DatabasePath[2..])
            : DatabasePath;
}
