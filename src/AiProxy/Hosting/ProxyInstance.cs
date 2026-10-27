using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AiProxy.Configuration;
using AiProxy.Core.Gain;

namespace AiProxy.Hosting;

/// <summary>Owns the process lease outside the host and its control pipe.</summary>
internal sealed class ProxyInstance(FileStream lease) : IDisposable
{
    private const int MarkerDiscoveryTimeoutMilliseconds = 3000;
    private const string LeaseFileName = "process.lock";
    private const string StartupFileName = "startup.lock";
    private const string RestartFlag = "--restart";

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



    private static StartupLock AcquireStartupLock(string directory)
    {
        try
        {
            return new StartupLock(new FileStream(Path.Combine(directory, StartupFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception)
        {
            throw new IOException("Another ai-proxy start or restart is in progress. Wait for it to finish, then retry.", exception);
        }
    }

    private static Uri[] ApiAddresses(IConfiguration configuration)
    {
        var urls = ExtractEndpointsFromKestrelConfig(configuration);
        return urls.Length > 0 ? urls : ExtractEndpointsByKnownProtocols(configuration);
    }

    private static Uri[] ExtractEndpointsByKnownProtocols(IConfiguration configuration)
    {
        var endpoints = new List<string>();
        foreach (var scheme in KnownNames.Network.Protocols)
        {
            var ports = (configuration[$"{scheme}_ports"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var port in ports)
            {
                endpoints.Add($"{scheme}://0.0.0.0:{port}");
            }
        }

        var combinedEndpoints = endpoints.Count > 0 ? string.Join(';', endpoints) : GainApi.DefaultEndpoint;
        return ConverRawEndpointAddresses(combinedEndpoints);
    }

    private static Uri[] ExtractEndpointsFromKestrelConfig(IConfiguration configuration)
    {
        var endpointUrls = configuration.GetSection(KnownNames.Configuration.KestrelEndpoints)
            .GetChildren()
            .Select(static endpoint => endpoint["Url"])
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .ToArray();

        var combinedUrls = endpointUrls.Length > 0 ? string.Join(';', endpointUrls) : configuration[KnownNames.Configuration.Urls];
        return string.IsNullOrWhiteSpace(combinedUrls) ? [] : ConverRawEndpointAddresses(combinedUrls);
    }

    private static Uri[] ConverRawEndpointAddresses(string urls) =>
    [
        .. urls.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(static url =>
        {
            const string defaultAddress = "://0.0.0.0:";

            var normalized = url
                .Replace("://*:", defaultAddress, StringComparison.Ordinal)
                .Replace("://+:", defaultAddress, StringComparison.Ordinal);

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var address) ||
                !KnownNames.Network.Protocols.Contains(address.Scheme) || address.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Query) ||
                !string.IsNullOrEmpty(address.Fragment) || address.Port is < 0 or > 65535 ||
                address is { Host: "localhost", Port: 0 })
            {
                throw new ArgumentException("The API URL configuration must contain HTTP(S) listener URLs with valid ports.");
            }

            return address;
        })
    ];

    private static void ProbeReplacement(IConfiguration configuration, ProxySettings settings, ProxyControl.ProxyInfo owner)
    {
        var owned = owner.ApiUrls.Select(static url => new Uri(url)).Append(new Uri(owner.ProxyUrl)).ToArray();
        if (settings.Port is { } port && owned.All(address => address.Port != port))
        {
            ProbePort(IPAddress.Loopback, port);
        }

        if (!HasApiConfiguration(configuration))
        {
            return;
        }

        foreach (var address in ApiAddresses(configuration).Where(address => owned.All(previous => previous.Port != address.Port)))
        {
            ProbePort(IPAddress.TryParse(address.Host, out var ip) ? ip : IPAddress.Any, address.Port);
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
            var pid = FindMarkerOwner(path);
            var owner = pid is null ? KnownNames.MarkerDiscovery.UnknownOwner : string.Format(CultureInfo.InvariantCulture, KnownNames.MarkerDiscovery.KnownOwnerFormat, pid);
            throw new IOException(string.Format(CultureInfo.InvariantCulture, KnownNames.MarkerDiscovery.StopOwnerFormat, owner), exception);
        }
    }

    internal static int? MarkerOwnerPid(string output)
    {
        var pids = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pids.Length == 0 || pids.Any(value => int.TryParse(value, out var pid) is false || pid <= 0))
        {
            return null;
        }

        int[] distinct = [.. pids.Select(value => int.Parse(value, CultureInfo.InvariantCulture)).Distinct()];
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static int? FindMarkerOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsMarkerOwner.Find(path);
        }

        var executable = OperatingSystem.IsMacOS() ? KnownNames.MarkerDiscovery.MacLsof : KnownNames.MarkerDiscovery.LinuxLsof;
        return FindUnixMarkerOwner(path, executable);
    }

    internal static int? FindUnixMarkerOwner(string path, string executable)
    {
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(KnownNames.MarkerDiscovery.ProcessIds);
            start.ArgumentList.Add(KnownNames.MarkerDiscovery.EndOptions);
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (process.WaitForExit(MarkerDiscoveryTimeoutMilliseconds) is false)
            {
                process.Kill();
                return null;
            }

            if (process.ExitCode is not 0)
            {
                return null;
            }

            _ = error.GetAwaiter().GetResult();
            return MarkerOwnerPid(output.GetAwaiter().GetResult());
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    internal static async Task<int> RunAsync(string[] arguments)
    {
        var startupLock = StartupLock.NoOpLock;
        try
        {
            string[] strings = [.. arguments.Where(static argument => argument != RestartFlag)];

            var builder = WebApplication.CreateBuilder(strings);

            var settings = builder.Configuration.GetSection(ProxySettings.SectionName).Get<ProxySettings>() ?? new ProxySettings();

            Validator.ValidateObject(settings, new ValidationContext(settings), true);

            _ = ApiAddresses(builder.Configuration);

            var directory = Path.GetFullPath(settings.DataDirectory ?? ProxyRuntimeOptions.DefaultDataDirectory);
            Directory.CreateDirectory(directory);

            startupLock = AcquireStartupLock(directory);

            var proxyInstance = await AcquireProxyInstance(arguments, directory, builder, settings);

            using var ownership = proxyInstance;

            ProbeRunningProxy(directory, builder);

            return await StartProxyApplication(directory, builder, startupLock);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ValidationException or ArgumentException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"ai-proxy: {exception.Message}");
            return 1;
        }
        finally
        {
            await startupLock.DisposeAsync();
        }
    }

    private static async Task<int> StartProxyApplication(string directory, WebApplicationBuilder builder, StartupLock startupLock)
    {
        await using var app = ProxyApplication.Create(builder);
        await app.StartAsync();
        using var process = Process.GetCurrentProcess();
        var proxyInfo = new ProxyControl.ProxyInfo("ai-proxy",
            ProxyControl.DefaultProtocol,
            Guid.CreateVersion7().ToString("N"),
            process.Id,
            process.StartTime.ToUniversalTime(),
            app.Services.GetRequiredService<TitaniumProxyService>().Address.AbsoluteUri.TrimEnd('/'),
            [.. app.Urls],
            new ProcessIdentity(process).StartToken);

        await using var control = new ProxyControl(directory, proxyInfo, app.Lifetime);
        await control.Start(startupLock);

        await app.WaitForShutdownAsync();
        return 0;
    }

    private static void ProbeRunningProxy(string directory, WebApplicationBuilder builder)
    {
        ProbeLegacyMarker(directory);
        ProbePort(IPAddress.Loopback, builder.GetProbeProxyPort());
        foreach (var address in ApiAddresses(builder.Configuration))
        {
            ProbePort(IPAddress.TryParse(address.Host, out var ip) ? ip : IPAddress.Any, address.Port);
        }
    }

    private static async Task<ProxyInstance> AcquireProxyInstance(string[] arguments, string directory, WebApplicationBuilder builder, ProxySettings settings)
    {
        ProxyInstance instance;
        try
        {
            instance = Acquire(directory);
        }
        catch (IOException) when (arguments.Contains(RestartFlag, StringComparer.Ordinal))
        {
            ProxyControl.ProxyInfo previous;
            try
            {
                previous = await ProxyControl.StopOwnedAsync(directory, CancellationToken.None, owner => ProbeReplacement(builder.Configuration, settings, owner));
            }
            catch (OperationCanceledException exception)
            {
                throw new IOException("The ai-proxy control peer did not finish shutdown within the deadline. Stop the owner manually, then retry.", exception);
            }

            if (settings.Port is null)
            {
                builder.Configuration[KnownNames.Configuration.ProxyPort] = new Uri(previous.ProxyUrl).Port.ToString(CultureInfo.InvariantCulture);
            }

            if (!HasApiConfiguration(builder.Configuration))
            {
                builder.WebHost.UseUrls(previous.ApiUrls);
            }

            // The startup claim excludes replacements; require the old process lease after its exact PID exits.
            instance = Acquire(directory);
        }

        return instance;
    }
}

file static class WebApplicationBuilderExtensions
{
    internal static int GetProbeProxyPort(this WebApplicationBuilder builder)
    {
        var portFromConfig = builder.Configuration[KnownNames.Configuration.ProxyPort];
        return string.IsNullOrEmpty(portFromConfig) ?
            ProxyRuntimeOptions.DefaultPort :
            int.Parse(portFromConfig, CultureInfo.InvariantCulture);
    }
}
