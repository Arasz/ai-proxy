using Titanium.Web.Proxy.Models;

namespace AiProxy.Hosting.Integration;

/// <summary>The operating-system side of <see cref="TitaniumSystemProxyRegistrar" />: what it captures, applies, restores and disarms.</summary>
public interface ISystemProxyPlatform
{
    /// <summary>Captures the current settings as the snapshot a later restore puts back.</summary>
    string Capture();

    /// <summary>Points HTTP and HTTPS at <paramref name="endPoint" /> and returns the settings now applied; throws after undoing a failed change.</summary>
    string Apply(ExplicitProxyEndPoint endPoint);

    /// <summary>Restores <paramref name="snapshot" /> only while the settings still belong to the run that applied <paramref name="appliedSnapshot" />.</summary>
    bool RestoreIfOwned(string endpoint, string snapshot, string? appliedSnapshot);

    /// <summary>Drops the saved settings, so stopping or exiting the server restores nothing.</summary>
    void Disarm();
}
