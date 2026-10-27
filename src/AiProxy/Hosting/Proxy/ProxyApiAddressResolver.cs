using AiProxy.Core.Gain;

namespace AiProxy.Hosting.Proxy;

public sealed class ProxyApiAddressResolver(IConfiguration configuration)
{
    public Uri[] ResolveProxyApiAddresses()
    {
        var urls = ExtractEndpointsFromKestrelConfig();
        return urls.Length > 0 ? urls : ExtractEndpointsByKnownProtocols();
    }

    private Uri[] ExtractEndpointsByKnownProtocols()
    {
        var endpoints = new List<string>();
        foreach (var scheme in KnownNames.Network.Protocols)
        {
            var ports = (configuration[$"{scheme}_ports"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var port in ports)
            {
                endpoints.Add($"{scheme}://0.0.0.0:{port}");
            }
        }

        var combinedEndpoints = endpoints.Count > 0 ? string.Join(';', endpoints) : GainApi.DefaultEndpoint;
        return ConverRawEndpointAddresses(combinedEndpoints);
    }

    private Uri[] ExtractEndpointsFromKestrelConfig()
    {
        var endpointUrls = configuration.GetSection(KnownNames.Configuration.KestrelEndpoints)
            .GetChildren()
            .Select(static endpoint => endpoint["Url"])
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .ToArray();

        var combinedUrls = endpointUrls.Length > 0 ? string.Join(';', endpointUrls) : configuration[KnownNames.Configuration.Urls];
        return string.IsNullOrWhiteSpace(combinedUrls) ? [] : ConverRawEndpointAddresses(combinedUrls);
    }

    private static Uri[] ConverRawEndpointAddresses(string urls) =>
    [
        .. urls.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(static url =>
        {
            const string defaultAddress = "://0.0.0.0:";

            var normalized = url
                .Replace("://*:", defaultAddress, StringComparison.Ordinal)
                .Replace("://+:", defaultAddress, StringComparison.Ordinal);

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var address) ||
                !KnownNames.Network.Protocols.Contains(address.Scheme) || address.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) ||
                !string.IsNullOrEmpty(address.Fragment) || address.Port is < 0 or > 65535 ||
                address is { Host: "localhost", Port: 0 })
            {
                throw new ArgumentException("The API URL configuration must contain HTTP(S) listener URLs with valid ports.");
            }

            return address;
        })
    ];
}
