namespace AiProxy.Client;

public static class ProxyClientServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddProxyHttpClient()
        {
            services.AddHttpClient<IProxyClient, ProxyClient>().ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { UseProxy = false });
            return services;
        }
    }
}
