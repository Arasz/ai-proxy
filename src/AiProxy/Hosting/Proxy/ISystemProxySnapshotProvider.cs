namespace AiProxy.Hosting.Proxy;

/// <summary>Captures and restores the current user's system proxy settings without changing unrelated settings.</summary>
public interface ISystemProxySnapshotProvider
{
    /// <summary>Returns a stable serialized snapshot of the settings this runtime can restore.</summary>
    string Capture();

    /// <summary>Returns whether the current HTTP(S) settings still point at the recorded endpoint.</summary>
    bool IsOwned(string endpoint);

    /// <summary>Returns whether the current settings still match this proxy run's endpoint and managed values.</summary>
    bool IsOwned(string endpoint, string snapshot);

    /// <summary>Restores the supplied snapshot only while current proxy settings still point at the recorded endpoint.</summary>
    Task<bool> RestoreIfOwned(string endpoint, string snapshot);
}
