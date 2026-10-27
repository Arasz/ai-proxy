using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AiProxy.Configuration;
using AiProxy.Hosting.Marker;

namespace AiProxy.Hosting.Proxy;

internal sealed class ProxyProbe(ProxyApiAddressResolver proxyApiAddressResolver, ProxySettings proxySettings, IConfiguration configuration)
{
    public void ProbeProxy(ProxyControl.ProxyInfo liveProxy)
    {
        ProbeLiveProxy(liveProxy);
        ProbePort(IPAddress.Loopback, GetProxyPort());
        foreach (var address in proxyApiAddressResolver.ResolveProxyApiAddresses())
        {
            ProbePort(IPAddress.TryParse(address.Host, out var ip) ? ip : IPAddress.Any, address.Port);
        }
    }

    private void ProbeLiveProxy(ProxyControl.ProxyInfo liveProxy)
    {
        var owned = liveProxy.ApiUrls.Select(static url => new Uri(url)).Append(new Uri(liveProxy.ProxyUrl)).ToArray();
        if (proxySettings.Port is { } port && owned.All(address => address.Port != port))
        {
            ProbePort(IPAddress.Loopback, port);
        }

        if (!configuration.HasApiConfiguration())
        {
            return;
        }

        foreach (var address in proxyApiAddressResolver.ResolveProxyApiAddresses().Where(address => owned.All(previous => previous.Port != address.Port)))
        {
            ProbePort(IPAddress.TryParse(address.Host, out var ip) ? ip : IPAddress.Any, address.Port);
        }
    }


    private static void ProbePort(IPAddress address, int port)
    {
        if (port == 0)
        {
            return;
        }

        try
        {
            using var probe = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            probe.ExclusiveAddressUse = true;
            probe.Bind(new IPEndPoint(address, port));
        }
        catch (SocketException exception)
        {
            throw new IOException($"Requested endpoint {address}:{port} is already in use. Stop its owner manually or choose another port; port occupancy cannot identify an ai-proxy owner.", exception);
        }
    }

    public async Task ProbeSystemProxyMarker(string markerDirectory, IMarkerOwnerSearch markerOwnerSearch)
    {
        var path = Path.Combine(markerDirectory, SystemProxyMarker.FileName);
        try
        {
            await using var marker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (FileNotFoundException)
        {
            // A fresh or cleanly stopped instance has no system-proxy marker.
        }
        catch (IOException exception)
        {
            var pid = await markerOwnerSearch.FindMarkerOwner(path);
            var owner = pid is null ? KnownNames.MarkerDiscovery.UnknownOwner : string.Format(CultureInfo.InvariantCulture, KnownNames.MarkerDiscovery.KnownOwnerFormat, pid);
            throw new IOException(string.Format(CultureInfo.InvariantCulture, KnownNames.MarkerDiscovery.StopOwnerFormat, owner), exception);
        }
    }

    private int GetProxyPort()
    {
        var portFromConfig = configuration[KnownNames.Configuration.ProxyPort];
        return string.IsNullOrEmpty(portFromConfig) ? ProxyRuntimeOptions.DefaultPort : int.Parse(portFromConfig, CultureInfo.InvariantCulture);
    }
}
