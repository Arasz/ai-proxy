using System.ComponentModel.DataAnnotations;
using AiProxy.Configuration;
using AiProxy.Core.Capture;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Handlers;
using AiProxy.Hosting;
using AiProxy.Storage;

namespace AiProxy;

/// <summary>Registers the proxy's services and maps its endpoints.</summary>
public static partial class ProxyServiceExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>Binds the <c>Proxy</c> section and registers the listener, the provider inspection it runs, and the settings they read.</summary>
        /// <param name="configuration">The configuration holding the <c>Proxy</c> section.</param>
        /// <param name="runtimeOptions">Where and how the listener runs; the configured port and certificate password replace its own.</param>
        public IServiceCollection AddProxy(IConfiguration configuration, ProxyRuntimeOptions? runtimeOptions = null)
        {
            var settings = configuration.GetSection(ProxySettings.SectionName).Get<ProxySettings>() ?? new ProxySettings();
            Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);

            var compression = settings.Compression;
            var options = runtimeOptions ?? ProxyRuntimeOptions.Default;
            if (settings.RootCertificatePassword is { Length: > 0 } rootPassword)
            {
                options = options with { RootCertificatePassword = rootPassword };
            }

            if (settings.Port is { } port)
            {
                options = options with { Port = port };
            }

            var capturePath = settings.Capture ? CapturePath(options) : null;

            services.AddSingleton(settings);
            services.AddSingleton(compression);
            services.AddSingleton(options);
            services.AddSingleton(TimeProvider.System);

            if (capturePath is not null)
            {
                services.AddSingleton<IRequestCaptureStore>(new SqliteCaptureStore(capturePath));
            }

            if (compression.Enabled)
            {
                services.AddCompression(compression);
            }
            else
            {
                services.AddSingleton<ICompressionPipeline, PassThroughCompressionPipeline>();
            }

            services.AddSingleton<IProviderHosts, CanonicalProviderHosts>();
            services.AddSingleton(provider => new ProviderRequestInspector(
                provider.GetRequiredService<ICompressionPipeline>(),
                provider.GetRequiredService<IProviderHosts>(),
                compression.Enabled,
                provider.GetRequiredService<ILogger<ProviderRequestInspector>>(),
                capturePath is null ? null : provider.GetRequiredService<IRequestCaptureStore>(),
                provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<TitaniumProxyService>();
            services.AddHostedService(provider => provider.GetRequiredService<TitaniumProxyService>());

            return services;
        }

        /// <summary>Registers the compression stage and everything it is composed of.</summary>
        /// <param name="settings">What the operator configured about the stage.</param>
        public IServiceCollection AddCompression(CompressionSettings settings)
        {
            var options = settings.Minimum is { } minimum
                ? CompressionOptions.WithMinimum(minimum)
                : CompressionOptions.Default;

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

            return services;
        }
    }

    extension(WebApplication app)
    {
        /// <summary>Adds request logging, the health endpoint, and the content endpoints when compression is on.</summary>
        public WebApplication UseProxy()
        {
            var settings = app.Services.GetRequiredService<ProxySettings>();
            if (settings.Capture)
            {
                var capturePath = CapturePath(app.Services.GetRequiredService<ProxyRuntimeOptions>());
                Log.CaptureOn(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ProxyServiceExtensions)), capturePath);
            }

            app.UseMiddleware<RequestLoggingMiddleware>();
            app.MapGet("/health", () => Results.Json(new { status = "ok" }));

            if (settings.Compression.Enabled)
            {
                app.MapContentEndpoints();
            }

            return app;
        }
    }

    private static string CapturePath(ProxyRuntimeOptions options) =>
        Path.Combine(options.DataDirectory, SqliteCaptureStore.FileName);

    private static partial class Log
    {
        [LoggerMessage(EventId = 1501, Level = LogLevel.Information, Message = "Request capture is on: every provider request body is stored in {CapturePath}")]
        public static partial void CaptureOn(ILogger logger, string capturePath);
    }
}
