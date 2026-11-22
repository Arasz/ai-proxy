using AiProxy.Ccr;
using AiProxy.Code;
using AiProxy.Configuration;
using AiProxy.Core.Compression;
using AiProxy.Core.Content;
using AiProxy.Core.Content.Ccr;
using AiProxy.Core.Content.Code;
using AiProxy.Core.Content.Handlers;

namespace AiProxy;

/// <summary>Registers the compression pipeline alone, with no store behind it; the caller supplies <see cref="IContentStore" /> and <see cref="IOriginalStore" />.</summary>
public static class CompressionPipelineExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>Registers the compression pipeline and everything it is composed of, and none of the SQLite adapters.</summary>
        /// <param name="settings">What the operator configured about the stage.</param>
        public IServiceCollection AddCompressionPipeline(CompressionSettings settings)
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
            services.AddSingleton<CcrPreviewer>();
            services.AddSingleton(options);
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

            return services;
        }
    }
}
