namespace AiProxy.Client;

public static class ProxyClientServiceCollectionExtensions
{
    /// <summary>The named HTTP client that calls the local proxy directly, never through a system proxy.</summary>
    public const string ProxyClientName = "ai-proxy";

    extension(IServiceCollection services)
    {
        public IServiceCollection AddProxyHttpClient()
        {
            services.AddHttpClient<IProxyClient, ProxyClient>(ProxyClientName).ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { UseProxy = false });
            return services;
        }
    }
}