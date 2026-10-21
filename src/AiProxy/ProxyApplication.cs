using AiProxy.Configuration;
using AiProxy.Core.Compression;
using AiProxy.Core.Providers;
using AiProxy.Core.Routing;
using AiProxy.Forwarding;
using AiProxy.Hosting;

namespace AiProxy;

/// <summary>Builds the proxy, so that a test can start the same host a person runs.</summary>
public static class ProxyApplication
{
    /// <summary>Composes the proxy and returns the host.</summary>
    /// <param name="builder">The host under construction.</param>
    /// <param name="configure">Applied after the default registrations, so a test can put something else in its place.</param>
    public static WebApplication Create(WebApplicationBuilder builder, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = ProxyOptions.FromConfiguration(builder.Configuration);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<PathDialectDetector>();
        builder.Services.AddSingleton<BodyDialectDetector>();
        builder.Services.AddSingleton<HeaderDialectDetector>();
        builder.Services.AddSingleton<CompositeDialectDetector>(services => new CompositeDialectDetector(
        [
            services.GetRequiredService<PathDialectDetector>(),
            services.GetRequiredService<BodyDialectDetector>(),
            services.GetRequiredService<HeaderDialectDetector>(),
        ]));
        builder.Services.AddSingleton<ICompressionPipeline, PassThroughCompressionPipeline>();
        builder.Services.AddSingleton<IPassThroughCatalog>(new PassThroughCatalog(options.PassThrough));
        builder.Services.AddSingleton<ICredentialProvider>(new ConfiguredCredentialProvider(options));
        builder.Services.AddSingleton<UpstreamCatalog>(new UpstreamCatalog(options.Upstreams));
        builder.Services.AddSingleton<ProviderRequestRouter>();
        builder.Services.AddSingleton<PassThroughRequestRouter>();
        builder.Services.AddSingleton<IRequestRouter>(services => new CompositeRequestRouter(
            services.GetRequiredService<ProviderRequestRouter>(),
            services.GetRequiredService<PassThroughRequestRouter>()));
        builder.Services.AddSingleton<IUpstreamForwarder, HttpUpstreamForwarder>();
        builder.Services.AddHttpClient("upstreams", client => client.Timeout = Timeout.InfiniteTimeSpan);

        configure?.Invoke(builder.Services);

        var app = builder.Build();

        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseMiddleware<ProxyForwardingMiddleware>();
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        return app;
    }
}
