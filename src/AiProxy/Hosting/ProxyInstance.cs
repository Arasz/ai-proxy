using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AiProxy.Configuration;

namespace AiProxy.Hosting;

/// <summary>Owns the process lease outside the host and its control pipe.</summary>
internal sealed class ProxyInstance(FileStream lease) : IDisposable
{
    internal const string LeaseFileName = "process.lock";

    public void Dispose()
    {
        lease.Dispose();
    }

    internal static ProxyInstance Acquire(string directory)
    {
        Directory.CreateDirectory(directory);
        return new ProxyInstance(new FileStream(Path.Combine(directory, LeaseFileName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None));
    }

    private static void ProbePort(IPAddress address, int port)
    {
        if (port == 0)
        {
            return;
        }

        try
        {
            using var probe = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            probe.ExclusiveAddressUse = true;
            probe.Bind(new IPEndPoint(address, port));
        }
        catch (SocketException exception)
        {
            throw new IOException($"Requested endpoint {address}:{port} is already in use. Stop its owner manually or choose another port; port occupancy cannot identify an ai-proxy owner.", exception);
        }
    }

    private static void ProbeLegacyMarker(string directory)
    {
        var path = Path.Combine(directory, SystemProxyMarker.FileName);
        try
        {
            using var marker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (FileNotFoundException)
        {
            // A fresh or cleanly stopped instance has no system-proxy marker.
        }
        catch (IOException exception)
        {
            throw new IOException("An older ai-proxy instance holds the system proxy marker. Stop it manually, then start ai-proxy again.", exception);
        }
    }

    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            var builder = WebApplication.CreateBuilder(arguments.Where(argument => argument != "--restart").ToArray());
            var settings = builder.Configuration.GetSection(ProxySettings.SectionName).Get<ProxySettings>() ?? new ProxySettings();
            Validator.ValidateObject(settings, new ValidationContext(settings), true);
            var directory = Path.GetFullPath(settings.DataDirectory ?? ProxyRuntimeOptions.DefaultDataDirectory);
            ProxyInstance instance;
            try
            {
                instance = Acquire(directory);
            }
            catch (IOException) when (arguments.Contains("--restart", StringComparer.Ordinal))
            {
                var previous = await ProxyControl.StopOwnedAsync(directory, CancellationToken.None).ConfigureAwait(false);
                if (settings.Port is null)
                {
                    builder.Configuration["Proxy:Port"] = new Uri(previous.ProxyUrl).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]) &&
                    string.IsNullOrWhiteSpace(builder.Configuration["http_ports"]) &&
                    string.IsNullOrWhiteSpace(builder.Configuration["https_ports"]) &&
                    !builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
                {
                    builder.WebHost.UseUrls(previous.ApiUrls);
                }

                // A competing restarter may win the lease. Never reconnect and stop that replacement.
                instance = Acquire(directory);
            }

            using var ownership = instance;
            ProbeLegacyMarker(directory);
            ProbePort(IPAddress.Loopback, int.Parse(builder.Configuration["Proxy:Port"] ?? "8080", System.Globalization.CultureInfo.InvariantCulture));
            await using var app = ProxyApplication.Create(builder);
            await app.StartAsync().ConfigureAwait(false);
            using var process = Process.GetCurrentProcess();
            var info = new ProxyControl.Info("ai-proxy", ProxyControl.Protocol, Guid.NewGuid().ToString("N"),
                process.Id, process.StartTime.ToUniversalTime(),
                app.Services.GetRequiredService<TitaniumProxyService>().Address.AbsoluteUri.TrimEnd('/'),
                app.Urls.ToArray());
            await using var control = new ProxyControl(info, app.Lifetime);
            control.Start(directory);
            await app.WaitForShutdownAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ValidationException or ArgumentException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"ai-proxy: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
