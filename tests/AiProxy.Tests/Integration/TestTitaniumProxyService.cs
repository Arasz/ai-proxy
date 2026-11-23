using AiProxy.Hosting;
using AiProxy.Hosting.Guard;
using AiProxy.Hosting.Integration;
using AiProxy.Hosting.Proxy;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network;

namespace AiProxy.Tests.Integration;

/// <summary>Builds a <see cref="TitaniumProxyService"/> with the production seams; a test overrides only the one it fakes.</summary>
internal static class TestTitaniumProxyService
{
    public static TitaniumProxyService Create(
        ProxyRuntimeOptions options,
        ProviderRequestInspector inspector,
        IProviderHosts hosts,
        ISystemProxyGuardLauncher guardLauncher,
        ILoggerFactory loggerFactory,
        TimeSpan markerWait,
        TimeProvider timeProvider,
        ISystemProxyRegistrar? registrar = null,
        Func<HttpWebClient, bool>? upstreamClosed = null,
        Func<CertificateManager, IRootCertificate>? rootCertificate = null) =>
        new(
            registrar is null ? static server => new TitaniumSystemProxyRegistrar(server) : _ => registrar,
            options,
            inspector,
            hosts,
            guardLauncher,
            loggerFactory,
            markerWait,
            timeProvider,
            upstreamClosed ?? UpstreamConnection.IsClosed,
            rootCertificate ?? (static certificates => new TitaniumRootCertificate(certificates)));
}
