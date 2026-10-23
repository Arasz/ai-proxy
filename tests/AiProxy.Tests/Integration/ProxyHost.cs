using AiProxy;
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

    private ProxyHost(WebApplication app)
    {
        _app = app;
    }

    /// <summary>The address the proxy listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The host services used by the integration fixture.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>Starts the proxy with the given configuration.</summary>
    public static async Task<ProxyHost> StartAsync(
        IReadOnlyDictionary<string, string> configuration,
        ICompressionPipeline? compression = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var proxyPort = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();

        foreach (var (key, value) in configuration)
        {
            builder.Configuration[key] = value;
        }

        var app = ProxyApplication.Create(
            builder,
            compression is null ? null : services => services.AddSingleton(compression),
            new ProxyRuntimeOptions(IPAddress.Loopback, proxyPort, ConfigureSystemProxy: false, TrustRootCertificate: false));

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();

        return new ProxyHost(app) { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    /// <summary>Stops the proxy.</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
