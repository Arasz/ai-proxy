using AiProxy.Configuration;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;
using AiProxy.Core.Providers;
using AiProxy.Core.Routing;
using AiProxy.Forwarding;
using AiProxy.Hosting;
using AiProxy.Storage;

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
        var compression = CompressionSettings.FromConfiguration(builder.Configuration);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(compression);
        builder.Services.AddSingleton<PathDialectDetector>();
        builder.Services.AddSingleton<BodyDialectDetector>();
        builder.Services.AddSingleton<HeaderDialectDetector>();
        builder.Services.AddSingleton<CompositeDialectDetector>(services => new CompositeDialectDetector(
        [
            services.GetRequiredService<PathDialectDetector>(),
            services.GetRequiredService<BodyDialectDetector>(),
            services.GetRequiredService<HeaderDialectDetector>(),
        ]));
        if (compression.Enabled)
        {
            AddCompression(builder.Services, compression);
        }
        else
        {
            builder.Services.AddSingleton<ICompressionPipeline, PassThroughCompressionPipeline>();
        }
        builder.Services.AddSingleton<IPassThroughCatalog>(new PassThroughCatalog(options.PassThrough));
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

        if (compression.Enabled)
        {
            app.MapContentEndpoints();
        }

        return app;
    }

    /// <summary>Registers the compression stage and everything it is composed of.</summary>
    /// <param name="services">The container to register into.</param>
    /// <param name="settings">What the operator configured about the stage.</param>
    private static void AddCompression(IServiceCollection services, CompressionSettings settings)
    {
        var options = settings.Minimum is { } minimum
            ? CompressionOptions.WithMinimum(minimum)
            : CompressionOptions.Default;

        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(settings.Store);
        services.AddSingleton<ContentQueue>();
        services.AddSingleton<IContentQueue>(provider => provider.GetRequiredService<ContentQueue>());
        services.AddSingleton<SqliteContentStore>();
        services.AddSingleton<IContentStore>(provider => provider.GetRequiredService<SqliteContentStore>());
        services.AddSingleton<IContentWriter>(provider => provider.GetRequiredService<SqliteContentStore>());
        services.AddSingleton<ToolOutputRegionFinder>();
        services.AddSingleton<IDecompositor, ContentDecompositor>();
        services.AddSingleton<ICompositor, ContentCompositor>();
        services.AddSingleton<ICompressionHandler, LogCompressionHandler>();
        services.AddSingleton<ICompressionHandler, JsonCompressionHandler>();
        services.AddSingleton<ICompressionHandler, XmlCompressionHandler>();
        services.AddSingleton<ICompressionHandler, TomlCompressionHandler>();
        services.AddSingleton<ICompressionHandler, MarkdownCompressionHandler>();
        services.AddSingleton<ICompressionHandler, SourceCodeCompressionHandler>();
        services.AddSingleton<ICompressionHandler, ProseCompressionHandler>();
        services.AddSingleton<ICompressionHandler, DirectoryListingCompressionHandler>();
        services.AddSingleton<ICompressionHandler, ShellTranscriptCompressionHandler>();
        services.AddSingleton<PassThroughCompressionHandler>();
        services.AddSingleton<IContentRouter>(provider => new ContentRouter(
            provider.GetServices<ICompressionHandler>(),
            provider.GetRequiredService<PassThroughCompressionHandler>()));
        services.AddSingleton<ICompressionExecutor, CompressionExecutor>();
        services.AddSingleton<ICompressionPipeline, ContentCompressionPipeline>();
        services.AddHostedService<ContentStoreWorker>();
    }
}
