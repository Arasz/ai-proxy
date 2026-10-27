namespace AiProxy.Hosting.Guard;

/// <summary>Starts the separate process that clears the system proxy if the owning process dies.</summary>
public interface ISystemProxyGuardLauncher
{
    /// <summary>Starts the guard for the marker in <paramref name="dataDirectory" />; throws when it cannot start. <paramref name="guardExited" /> runs if the guard ends before the session is disposed.</summary>
    ISystemProxyGuardSession Launch(string dataDirectory, Action guardExited);
}

/// <summary>The owner's end of a running guard; disposing without reporting a restore makes the guard clear the proxy.</summary>
public interface ISystemProxyGuardSession : IDisposable
{
    /// <summary>Tells the guard the user's own proxy settings are back, so it must leave them alone; throws <see cref="IOException" /> when the guard is gone.</summary>
    void ReportRestored();
}