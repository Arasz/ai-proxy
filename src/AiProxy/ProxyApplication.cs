using AiProxy.Core.Gain;
using AiProxy.Hosting;
using AiProxy.Hosting.Proxy;

namespace AiProxy;

/// <summary>Builds the proxy so that a test can start the same host a person runs.</summary>
public static class ProxyApplication
{
    /// <summary>Composes the proxy and returns the host.</summary>
    /// <param name="builder">The host under construction.</param>
    /// <param name="configure">Applied after the default registrations, so a test can put something else in its place.</param>
    /// <param name="runtimeOptions"> The runtime options for the proxy.</param>
    public static WebApplication Create(
        WebApplicationBuilder builder,
        Action<IServiceCollection>? configure = null,
        ProxyRuntimeOptions? runtimeOptions = null)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration[KnownNames.Configuration.Urls]) &&
            string.IsNullOrWhiteSpace(builder.Configuration[KnownNames.Configuration.HttpPorts]) &&
            string.IsNullOrWhiteSpace(builder.Configuration[KnownNames.Configuration.HttpsPorts]) &&
            !builder.Configuration.GetSection(KnownNames.Configuration.KestrelEndpoints).GetChildren().Any())
        {
            builder.WebHost.UseUrls(GainApi.DefaultEndpoint);
        }

        if (string.IsNullOrWhiteSpace(builder.Configuration[KnownNames.Configuration.AllowedHosts]))
            // Only loopback names: a page rebound to 127.0.0.1 sends its own host and is refused.
        {
            builder.Configuration[KnownNames.Configuration.AllowedHosts] = KnownNames.Network.LoopbackHosts;
        }

        builder.Services.AddProxy(builder.Configuration, runtimeOptions);

        configure?.Invoke(builder.Services);

        return builder.Build().UseProxy();
    }
}
