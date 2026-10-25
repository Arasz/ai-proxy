using AiProxy.Configuration;
using AiProxy.Ccr;
using AiProxy.Core.Capture;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;
using AiProxy.Core.Content.Ccr;
using AiProxy.Hosting;
using AiProxy.Storage;

namespace AiProxy;

/// <summary>Builds the proxy so that a test can start the same host a person runs.</summary>
public static partial class ProxyApplication
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
        ArgumentNullException.ThrowIfNull(builder);

        var compression = CompressionSettings.FromConfiguration(builder.Configuration);

        builder.Services.AddSingleton(compression);
        var proxyOptions = runtimeOptions ?? ProxyRuntimeOptions.Default;
        if (builder.Configuration["PROXY_ROOT_CERTIFICATE_PASSWORD"] is { Length: > 0 } rootPassword)
        {
            proxyOptions = proxyOptions with { RootCertificatePassword = rootPassword };
        }

        if (builder.Configuration["PROXY_PORT"] is { } portValue)
        {
            proxyOptions = proxyOptions with { Port = ParsePort(portValue) };
        }

        builder.Services.AddSingleton(proxyOptions);
        builder.Services.AddSingleton(TimeProvider.System);
        var capturePath = ConfigurationSwitch.IsOn(builder.Configuration["PROXY_CAPTURE"])
            ? Path.Combine(proxyOptions.DataDirectory, SqliteCaptureStore.FileName)
            : null;
        if (capturePath is not null)
        {
            builder.Services.AddSingleton<IRequestCaptureStore>(new SqliteCaptureStore(capturePath));
        }

        if (compression.Enabled)
        {
            AddCompression(builder.Services, compression);
        }
        else
        {
            builder.Services.AddSingleton<ICompressionPipeline, PassThroughCompressionPipeline>();
        }
        builder.Services.AddSingleton<IProviderHosts, CanonicalProviderHosts>();
        builder.Services.AddSingleton(services => new ProviderRequestInspector(
            services.GetRequiredService<ICompressionPipeline>(),
            services.GetRequiredService<IProviderHosts>(),
            compression.Enabled,
            services.GetRequiredService<ILogger<ProviderRequestInspector>>(),
            capturePath is null ? null : services.GetRequiredService<IRequestCaptureStore>(),
            services.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<TitaniumProxyService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<TitaniumProxyService>());

        configure?.Invoke(builder.Services);

        var app = builder.Build();
        if (capturePath is not null)
        {
            Log.CaptureOn(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ProxyApplication)), capturePath);
        }

        app.UseMiddleware<RequestLoggingMiddleware>();
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        if (compression.Enabled)
        {
            app.MapContentEndpoints();
        }

        return app;
    }

    /// <summary>Reads <c>PROXY_PORT</c>; a value that is not a port number stops startup instead of falling back.</summary>
    private static int ParsePort(string value) =>
        int.TryParse(value.Trim(), out var port) && port is >= 1 and <= 65535
            ? port
            : throw new InvalidOperationException($"PROXY_PORT must be a port number from 1 to 65535, but was '{value}'.");

    /// <summary>Registers the compression stage and everything it is composed of.</summary>
    /// <param name="services">The container to register into.</param>
    /// <param name="settings">What the operator configured about the stage.</param>
    private static void AddCompression(IServiceCollection services, CompressionSettings settings)
    {
        var options = settings.Minimum is { } minimum
            ? CompressionOptions.WithMinimum(minimum)
            : CompressionOptions.Default;

        services.AddTransient<CcrPreviewOverlay>();
        services.AddTransient<CcrPreviewBuilder>();
        services.AddSingleton<IToolArgumentSchema, RetrievalArgumentSchema>();
        services.AddTransient<CcrRetrievalParser>();
        services.AddTransient<CcrRetriever>();
        services.AddSingleton(options);
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 1501, Level = LogLevel.Information, Message = "Request capture is on: every provider request body is stored in {CapturePath}")]
        public static partial void CaptureOn(ILogger logger, string capturePath);
    }
}
