using AiProxy.Configuration;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;
using AiProxy.Hosting;
using AiProxy.Storage;

namespace AiProxy;

/// <summary>Builds the proxy, so that a test can start the same host a person runs.</summary>
public static class ProxyApplication
{
    /// <summary>Composes the proxy and returns the host.</summary>
    /// <param name="builder">The host under construction.</param>
    /// <param name="configure">Applied after the default registrations, so a test can put something else in its place.</param>
    public static WebApplication Create(
        WebApplicationBuilder builder,
        Action<IServiceCollection>? configure = null,
        ProxyRuntimeOptions? runtimeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var compression = CompressionSettings.FromConfiguration(builder.Configuration);

        builder.Services.AddSingleton(compression);
        var proxyOptions = runtimeOptions ?? ProxyRuntimeOptions.Default;
        if (builder.Configuration["PROXY_ROOT_CERTIFICATE_PASSWORD"] is { Length: > 0 } rootPassword)
        {
            proxyOptions = proxyOptions with { RootCertificatePassword = rootPassword };
        }

        builder.Services.AddSingleton(proxyOptions);
        if (compression.Enabled)
        {
            AddCompression(builder.Services, compression);
        }
        else
        {
            builder.Services.AddSingleton<ICompressionPipeline, PassThroughCompressionPipeline>();
        }
        builder.Services.AddSingleton<ProviderRequestInspector>();
        builder.Services.AddSingleton<TitaniumProxyService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<TitaniumProxyService>());

        configure?.Invoke(builder.Services);

        var app = builder.Build();

        app.UseMiddleware<RequestLoggingMiddleware>();
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
