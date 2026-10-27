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
    internal const string StartupFileName = "startup.lock";

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

    private static FileStream AcquireStartup(string directory)
    {
        try
        {
            return new FileStream(Path.Combine(directory, StartupFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new IOException("Another ai-proxy start or restart is in progress. Wait for it to finish, then retry.", exception);
        }
    }

    private static Uri[] ApiAddresses(IConfiguration configuration)
    {
        var endpoints = configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Select(endpoint => endpoint["Url"]).Where(url => !string.IsNullOrWhiteSpace(url)).ToArray();
        var urls = endpoints.Length > 0 ? string.Join(';', endpoints) : configuration["urls"];
        if (string.IsNullOrWhiteSpace(urls))
        {
            var ports = new List<string>();
            foreach (var scheme in new[] { "http", "https" })
            {
                foreach (var port in (configuration[scheme + "_ports"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    ports.Add($"{scheme}://0.0.0.0:{port}");
                }
            }

            urls = ports.Count > 0 ? string.Join(';', ports) : Core.Gain.GainApi.DefaultEndpoint;
        }

        return urls.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(url =>
        {
            var normalized = url.Replace("://*:", "://0.0.0.0:", StringComparison.Ordinal)
                .Replace("://+:", "://0.0.0.0:", StringComparison.Ordinal);
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var address) ||
                address.Scheme is not ("http" or "https") || address.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) ||
                !string.IsNullOrEmpty(address.Fragment) || address.Port is < 0 or > 65535 ||
                address.Host == "localhost" && address.Port == 0)
            {
                throw new ArgumentException("The API URL configuration must contain HTTP(S) listener URLs with valid ports.");
            }

            return address;
        }).ToArray();
    }

    private static void ProbeReplacement(IConfiguration configuration, ProxySettings settings, ProxyControl.Info owner)
    {
        var owned = owner.ApiUrls.Select(url => new Uri(url)).Append(new Uri(owner.ProxyUrl)).ToArray();
        if (settings.Port is { } port && owned.All(address => address.Port != port))
        {
            ProbePort(IPAddress.Loopback, port);
        }

        if (HasApiConfiguration(configuration))
        {
            foreach (var address in ApiAddresses(configuration).Where(address => owned.All(previous => previous.Port != address.Port)))
            {
                ProbePort(IPAddress.TryParse(address.Host, out var ip) ? ip : IPAddress.Any, address.Port);
            }
        }
    }

    private static bool HasApiConfiguration(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["urls"]) ||
               !string.IsNullOrWhiteSpace(configuration["http_ports"]) ||
               !string.IsNullOrWhiteSpace(configuration["https_ports"]) ||
               configuration.GetSection("Kestrel:Endpoints").GetChildren().Any();
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
            _ = ApiAddresses(builder.Configuration);
            var directory = Path.GetFullPath(settings.DataDirectory ?? ProxyRuntimeOptions.DefaultDataDirectory);
            Directory.CreateDirectory(directory);
            using var startup = AcquireStartup(directory);
            ProxyInstance instance;
            try
            {
                instance = Acquire(directory);
            }
            catch (IOException) when (arguments.Contains("--restart", StringComparer.Ordinal))
            {
                var previous = await ProxyControl.StopOwnedAsync(directory, CancellationToken.None, owner => ProbeReplacement(builder.Configuration, settings, owner)).ConfigureAwait(false);
                if (settings.Port is null)
                {
                    builder.Configuration["Proxy:Port"] = new Uri(previous.ProxyUrl).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                if (!HasApiConfiguration(builder.Configuration))
                {
                    builder.WebHost.UseUrls(previous.ApiUrls);
                }

                // A competing restarter may win the lease. Never reconnect and stop that replacement.
                instance = Acquire(directory);
            }

            using var ownership = instance;
            ProbeLegacyMarker(directory);
            ProbePort(IPAddress.Loopback, int.Parse(builder.Configuration["Proxy:Port"] ?? "8080", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var address in ApiAddresses(builder.Configuration))
            {
                ProbePort(IPAddress.TryParse(address.Host, out var ip) ? ip : IPAddress.Any, address.Port);
            }
            await using var app = ProxyApplication.Create(builder);
            await app.StartAsync().ConfigureAwait(false);
            using var process = Process.GetCurrentProcess();
            var info = new ProxyControl.Info("ai-proxy", ProxyControl.Protocol, Guid.NewGuid().ToString("N"),
                process.Id, process.StartTime.ToUniversalTime(),
                app.Services.GetRequiredService<TitaniumProxyService>().Address.AbsoluteUri.TrimEnd('/'),
                app.Urls.ToArray());
            await using var control = new ProxyControl(directory, info, app.Lifetime);
            startup.Dispose();
            control.Start();
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
