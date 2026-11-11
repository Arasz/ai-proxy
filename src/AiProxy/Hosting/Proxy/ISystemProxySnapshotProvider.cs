namespace AiProxy.Hosting.Proxy;

/// <summary>Captures and restores the current user's system proxy settings without changing unrelated settings.</summary>
public interface ISystemProxySnapshotProvider
{
    /// <summary>Returns a stable serialized snapshot of the settings this runtime can restore.</summary>
    string Capture();

    /// <summary>Returns whether the current HTTP(S) settings still point at the recorded endpoint.</summary>
    bool IsOwned(string endpoint);

    /// <summary>
    ///     Returns whether the current settings still match this proxy run's endpoint and managed values;
    ///     <paramref name="appliedSnapshot" /> is what the run applied, or null when it was never recorded.
    /// </summary>
    bool IsOwned(string endpoint, string snapshot, string? appliedSnapshot);

    /// <summary>Restores <paramref name="snapshot" /> only while <see cref="IsOwned(string, string, string)" /> holds.</summary>
    Task<bool> RestoreIfOwned(string endpoint, string snapshot, string? appliedSnapshot);
}
