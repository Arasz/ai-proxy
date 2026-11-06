using AiProxy.Hosting.Marker;
using AiProxy.Hosting.Proxy;

namespace AiProxy.Hosting.Guard;

/// <summary>Restores a saved system proxy snapshot only while the recorded owner settings remain current.</summary>
public sealed class SystemProxyRecovery(ISystemProxySnapshotProvider snapshotProvider)
{
    /// <summary>Restores the exact prior settings when the current settings still match the previous proxy run.</summary>
    public async Task<bool> RestoreIfOwned(SystemProxyMarker marker)
    {
        var previous = marker.PreviousSettingsSnapshot;
        var endpoint = marker.PreviousEndpoint;
        if (previous is null || endpoint is null)
        {
            return false;
        }

        return await snapshotProvider.RestoreIfOwned(endpoint, previous);
    }
}
