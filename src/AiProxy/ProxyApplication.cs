using AiProxy.Hosting;

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
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]) &&
            string.IsNullOrWhiteSpace(builder.Configuration["HTTP_PORTS"]) &&
            string.IsNullOrWhiteSpace(builder.Configuration["HTTPS_PORTS"]) &&
            !builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        {
            builder.WebHost.UseUrls(GainEndpoints.DefaultEndpoint);
        }

        builder.Services.AddProxy(builder.Configuration, runtimeOptions);

        configure?.Invoke(builder.Services);

        return builder.Build().UseProxy();
    }
}
