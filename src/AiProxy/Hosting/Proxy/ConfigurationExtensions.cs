namespace AiProxy.Hosting.Proxy;

internal static class ConfigurationExtensions
{
    extension(IConfiguration configuration)
    {
        internal bool HasApiConfiguration() =>
            !string.IsNullOrWhiteSpace(configuration[KnownNames.Configuration.Urls]) ||
            !string.IsNullOrWhiteSpace(configuration[KnownNames.Configuration.HttpPorts]) ||
            !string.IsNullOrWhiteSpace(configuration[KnownNames.Configuration.HttpsPorts]) ||
            configuration.GetSection(KnownNames.Configuration.KestrelEndpoints).GetChildren().Any();
    }
}
