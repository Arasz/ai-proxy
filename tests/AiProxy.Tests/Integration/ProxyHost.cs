using AiProxy.Core.Capture;
using AiProxy.Core.Compression;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Sockets;
using AiProxy.Hosting;

namespace AiProxy.Tests.Integration;

/// <summary>The proxy under test: the same host a person runs, on a free loopback port.</summary>
internal sealed class ProxyHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TemporaryDirectory _directory;

    private ProxyHost(WebApplication app, TemporaryDirectory directory)
    {
        _app = app;
        _directory = directory;
    }

    /// <summary>The address the proxy listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The forward-proxy listener clients send provider traffic through.</summary>
    public Uri ProxyAddress => _app.Services.GetRequiredService<TitaniumProxyService>().Address;

    /// <summary>The private data directory the proxy was given.</summary>
    public string DataDirectory => _directory.Path;

    /// <summary>The host services used by the integration fixture.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>Starts the proxy with the given configuration.</summary>
    public static async Task<ProxyHost> StartAsync(
        IReadOnlyDictionary<string, string> configuration,
        ICompressionPipeline? compression = null,
        IProviderHosts? providerHosts = null,
        IRequestCaptureStore? capture = null,
        ILoggerProvider? logging = null,
        TimeProvider? time = null,
        Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (logging is not null)
        {
            builder.Logging.AddProvider(logging);
        }
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var proxyPort = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();

        // Compression is on by default for a person; a test turns it on and names its own database.
        builder.Configuration["Proxy:Compression:Enabled"] = "false";
        foreach (var (key, value) in configuration)
        {
            builder.Configuration[key] = value;
        }

        var directory = new TemporaryDirectory();
        var app = ProxyApplication.Create(
            builder,
            services =>
            {
                if (compression is not null)
                {
                    services.AddSingleton(compression);
                }

                if (providerHosts is not null)
                {
                    services.AddSingleton(providerHosts);
                }

                if (capture is not null)
                {
                    services.AddSingleton(capture);
                }

                if (time is not null)
                {
                    services.AddSingleton(time);
                }

                configure?.Invoke(services);
            },
            new ProxyRuntimeOptions(IPAddress.Loopback, proxyPort, ConfigureSystemProxy: false, TrustRootCertificate: false, directory.Path));

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();

        return new ProxyHost(app, directory) { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    /// <summary>Stops the proxy.</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _directory.Dispose();
    }
}
