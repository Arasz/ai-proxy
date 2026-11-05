namespace AiProxy.Hosting.Proxy;

/// <summary>Gracefully stops the owned proxy of a data directory through its control pipe; never kills and never probes ports.</summary>
internal sealed class ProxyStop(ProxyLease lease)
{
    /// <summary>Returns the stopped owner, or null when none runs. Holds the startup claim throughout, so a start or restart fails fast meanwhile.</summary>
    public async Task<ProxyControl.ProxyInfo?> StopAsync(string directory, CancellationToken token, Action<ProxyControl.ProxyInfo>? beforeStop = null)
    {
        await using var claim = AcquireClaimOrFail(directory);
        if (RuntimeLeaseIsFree(directory))
        {
            return null;
        }

        try
        {
            return await ProxyControl.StopOwnedAsync(directory, token, beforeStop);
        }
        catch (Exception exception) when (exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new IOException("The ai-proxy control peer did not answer or finish shutdown within the deadline. Stop the owner manually.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new IOException("The ai-proxy owner process is gone but still holds the lease. Stop it manually.", exception);
        }
    }

    private StartupLease AcquireClaimOrFail(string directory)
    {
        try
        {
            return lease.AcquireStartupLease(directory);
        }
        catch (IOException exception)
        {
            throw new IOException("Another ai-proxy start, restart or stop is in progress. Wait for it to finish, then retry.", exception);
        }
    }

    private bool RuntimeLeaseIsFree(string directory)
    {
        try
        {
            lease.AcquireRuntimeLease(directory).File.Dispose();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
