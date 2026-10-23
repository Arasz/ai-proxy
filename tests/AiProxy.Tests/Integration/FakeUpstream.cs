using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace AiProxy.Tests.Integration;

/// <summary>An upstream that records what it received, so a test can assert on the wire.</summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _gate = new();

    private FakeUpstream(WebApplication app)
    {
        _app = app;
    }

    /// <summary>Every request the upstream received, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>The address the upstream listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>What the upstream answers with. The default is a short plain body.</summary>
    public Func<FakeUpstream, HttpContext, Task> Handler { get; set; } =
        static (_, context) => context.Response.WriteAsync("upstream ok");

    /// <summary>Starts an upstream on a free loopback port, over HTTPS when a server certificate is given.</summary>
    public static async Task<FakeUpstream> StartAsync(X509Certificate2? certificate = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (certificate is null)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }
        else
        {
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        }

        var app = builder.Build();
        var upstream = new FakeUpstream(app);
        app.Run(upstream.HandleAsync);

        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        upstream.BaseAddress = new Uri(addresses!.Addresses.First());

        return upstream;
    }

    /// <summary>Stops the upstream.</summary>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        var headers = context.Request.Headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            _requests.Add(new RecordedRequest(
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                context.Request.QueryString.Value ?? string.Empty,
                headers,
                body));
        }

        await Handler(this, context);
    }
}
