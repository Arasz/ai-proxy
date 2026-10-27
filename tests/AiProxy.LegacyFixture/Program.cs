using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

var directory = Environment.GetEnvironmentVariable("LEGACY_PROXY_DIRECTORY")!;
Directory.CreateDirectory(directory);
if (args.FirstOrDefault() == "--guard-system-proxy")
{
    var path = Path.Combine(directory, "system-proxy.lock");
    await using (var marker = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        Console.WriteLine("Application started LEGACY guard");
        await Task.Delay(TimeSpan.FromSeconds(8));
    }

    File.Delete(path);
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var ignoreTermination = Environment.GetEnvironmentVariable("LEGACY_IGNORE_TERMINATION") == "true";
if (ignoreTermination)
{
    builder.Services.AddSingleton<IHostLifetime, NonStoppingLifetime>();
}

using var signal = ignoreTermination && !OperatingSystem.IsWindows()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true)
    : null;
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
if (Environment.GetEnvironmentVariable("LEGACY_HUNG_API") == "true")
{
    app.MapGet("/health", async (CancellationToken token) => await Task.Delay(Timeout.InfiniteTimeSpan, token));
}
else
{
    app.MapGet("/health", () => new { status = "ok" });
}
using var proxy = new TcpListener(IPAddress.Loopback, 0);
proxy.Start();
var port = ((IPEndPoint)proxy.LocalEndpoint).Port;
var markerPath = Path.Combine(directory, "system-proxy.lock");
await using (var marker = new FileStream(markerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
{
    await marker.WriteAsync(Encoding.UTF8.GetBytes($"127.0.0.1:{port}"));
    await marker.FlushAsync();
    await app.StartAsync();
    Console.WriteLine("Application started LEGACY " + JsonSerializer.Serialize(new
    {
        proxy = $"http://127.0.0.1:{port}", api = app.Urls.Single()
    }));
    await app.WaitForShutdownAsync();
}

File.Delete(markerPath);

internal sealed class NonStoppingLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
