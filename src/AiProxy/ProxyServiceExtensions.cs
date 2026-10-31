using System.ComponentModel.DataAnnotations;
using AiProxy.Ccr;
using AiProxy.Code;
using AiProxy.Configuration;
using AiProxy.Core.Capture;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Code;
using AiProxy.Core.Content.Handlers;
using AiProxy.Core.Gain;
using AiProxy.Hosting;
using AiProxy.Hosting.Api;
using AiProxy.Hosting.Proxy;
using AiProxy.Storage;
using CaptureEvictionWorker = AiProxy.Hosting.Integration.CaptureEvictionWorker;
using ContentStoreWorker = AiProxy.Hosting.Integration.ContentStoreWorker;
using ProviderRequestInspector = AiProxy.Hosting.Integration.ProviderRequestInspector;
using RequestLoggingMiddleware = AiProxy.Hosting.Api.RequestLoggingMiddleware;
using TitaniumProxyService = AiProxy.Hosting.Integration.TitaniumProxyService;

namespace AiProxy;

/// <summary>Registers the proxy's services and maps its endpoints.</summary>
public static partial class ProxyServiceExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>Binds the <c>Proxy</c> section and registers the listener, the provider inspection it runs, and the settings they read.</summary>
        /// <param name="configuration">The configuration holding the <c>Proxy</c> section.</param>
        /// <param name="runtimeOptions">Runtime defaults used when the corresponding Proxy setting is unset.</param>
        public IServiceCollection AddProxy(IConfiguration configuration, ProxyRuntimeOptions? runtimeOptions = null)
        {
            var settings = ProxySettings.Load(configuration);

            var compression = settings.Compression;
            var defaults = runtimeOptions ?? ProxyRuntimeOptions.Default;
            var options = defaults with
            {
                DataDirectory = settings.DataDirectory ?? defaults.DataDirectory,
                ConfigureSystemProxy = settings.ConfigureSystemProxy ?? defaults.ConfigureSystemProxy,
                TrustRootCertificate = settings.TrustRootCertificate ?? defaults.TrustRootCertificate
            };
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
            services.AddProblemDetails();
            services.AddSingleton(new GainStoreOptions(Path.Combine(options.DataDirectory, "gain.db")));
            services.AddSingleton<IGainStore, SqliteGainStore>();

            if (capturePath is null)
            {
                services.AddSingleton<IRequestCaptureStore>(new NoRequestCapture());
            }
            else
            {
                services.AddSingleton(provider => new SqliteCaptureStore(
                    capturePath,
                    settings.CaptureMaxBytes ?? SqliteCaptureStore.DefaultMaxLiveBytes,
                    provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILogger<SqliteCaptureStore>>()));
                services.AddSingleton<IRequestCaptureStore>(provider => provider.GetRequiredService<SqliteCaptureStore>());
                services.AddHostedService<CaptureEvictionWorker>();
            }

            if (compression.Enabled)
            {
                services.AddCompression(compression);
                services.AddCcr(settings.Ccr);
            }
            else
            {
                services.AddSingleton<ICompressionPipeline, PassThroughCompressionPipeline>();
                services.AddSingleton<ICcrRuntime, CcrOff>();
            }

            services.AddSingleton<IProviderHosts, CanonicalProviderHosts>();
            services.AddSingleton<ProviderRequestInspector>();
            services.AddSingleton<TitaniumProxyService>();
            services.AddHostedService(static provider => provider.GetRequiredService<TitaniumProxyService>());

            return services;
        }

        /// <summary>Registers the CCR relay, the eviction of its originals, and what the inspector runs it with.</summary>
        /// <param name="settings">What the operator configured about CCR.</param>
        public IServiceCollection AddCcr(CcrSettings settings)
        {
            services.AddSingleton(settings);
            services.AddSingleton<CcrRelay>();
            services.AddSingleton<ICcrRuntime, CcrRuntime>();
            services.AddHostedService<OriginalEvictionWorker>();

            return services;
        }

        /// <summary>Registers the compression stage and everything it is composed of.</summary>
        /// <param name="settings">What the operator configured about the stage.</param>
        public IServiceCollection AddCompression(CompressionSettings settings)
        {
            var options = settings.Minimum is { } minimum
                ? CompressionOptions.WithMinimum(minimum)
                : CompressionOptions.Default;

            services.AddTransient<CcrPreviewOverlay>();
            services.AddTransient<CcrPreviewBuilder>();
            services.AddSingleton<IToolArgumentSchema, RetrievalArgumentSchema>();
            services.AddTransient<CcrRetrievalParser>();
            services.AddTransient<CcrRetriever>();
            services.AddSingleton(CcrOptions.Default);
            services.AddSingleton<Func<CcrPreviewBuilder>>(provider => provider.GetRequiredService<CcrPreviewBuilder>);
            services.AddSingleton<Func<CcrRetriever>>(provider => provider.GetRequiredService<CcrRetriever>);
            services.AddSingleton<SqliteOriginalStore>();
            services.AddSingleton<IOriginalStore>(provider => provider.GetRequiredService<SqliteOriginalStore>());
            services.AddSingleton<CcrPreviewer>();
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
            if (settings.CodeViews)
            {
                services.AddKeyedSingleton<TreeSitterLanguage>(TreeSitterLanguage.PythonKey, static (provider, _) =>
                    new TreeSitterLanguage(TreeSitterLanguage.PythonKey, provider.GetRequiredService<ILogger<TreeSitterLanguage>>()));
                services.AddKeyedSingleton<TreeSitterLanguage>(TreeSitterLanguage.JavaScriptKey, static (provider, _) =>
                    new TreeSitterLanguage(TreeSitterLanguage.JavaScriptKey, provider.GetRequiredService<ILogger<TreeSitterLanguage>>()));
                services.AddKeyedSingleton<TreeSitterLanguage>(TreeSitterLanguage.TypeScriptKey, static (provider, _) =>
                    new TreeSitterLanguage(TreeSitterLanguage.TypeScriptKey, provider.GetRequiredService<ILogger<TreeSitterLanguage>>()));
                services.AddKeyedSingleton<TreeSitterLanguage>(TreeSitterLanguage.TsxKey, static (provider, _) =>
                    new TreeSitterLanguage(TreeSitterLanguage.TsxKey, provider.GetRequiredService<ILogger<TreeSitterLanguage>>()));
                services.AddSingleton<ICodeLexer, CSharpCodeLexer>();
                services.AddSingleton<ICodeLexer, PythonCodeLexer>();
                services.AddSingleton<ICodeLexer, JavaScriptCodeLexer>();
                services.AddSingleton<ICodeLexer, TypeScriptCodeLexer>();
            }

            services.AddSingleton<ICompressionHandler, SourceCodeCompressionHandler>();
            services.AddSingleton<ICompressionHandler, ProseCompressionHandler>();
            services.AddSingleton<ICompressionHandler, DirectoryListingCompressionHandler>();
            services.AddSingleton<ICompressionHandler, ShellTranscriptCompressionHandler>();
            services.AddSingleton<ICompressionHandler, BuildOutputCompressionHandler>();
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

            app.UseExceptionHandler();
            app.UseStatusCodePages();
            app.UseMiddleware<RequestLoggingMiddleware>();
            app.MapGet("/health", () => Results.Json(new { status = "ok" }));
            app.MapGainEndpoints();

            if (settings.Compression.Enabled)
            {
                app.MapContentEndpoints();
            }

            return app;
        }
    }

    private static string CapturePath(ProxyRuntimeOptions options)
    {
        return Path.Combine(options.DataDirectory, SqliteCaptureStore.FileName);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1501, Level = LogLevel.Information, Message = "Request capture is on: every provider request body is stored in {CapturePath}")]
        public static partial void CaptureOn(ILogger logger, string capturePath);
    }
}
