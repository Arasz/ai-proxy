using System.ComponentModel.DataAnnotations;
using AiProxy.Configuration;
using AiProxy.Hosting.Marker;

namespace AiProxy.Hosting.Proxy;

/// <summary>Owns the process lease outside the host and its control pipe.</summary>
internal static class ProxyInstance
{
    public static async Task<int> RunAsync(string[] arguments, IMarkerOwnerSearch markerOwnerSearch, CancellationToken cancellationToken = default)
    {
        try
        {
            string[] filteredArguments = [.. arguments.Where(static argument => argument != ProxyCommand.RestartFlag)];
            var builder = WebApplication.CreateBuilder(filteredArguments);
            var settings = builder.Configuration.GetSection(ProxySettings.SectionName).Get<ProxySettings>() ?? new ProxySettings();

            Validator.ValidateObject(settings, new ValidationContext(settings), true);

            var proxyApiAddressResolver = new ProxyApiAddressResolver(builder.Configuration);
            var proxyProbe = new ProxyProbe(proxyApiAddressResolver, settings, builder.Configuration);
            var directory = EnsureDataDirectory(settings);
            await using var proxyInstanceLifetime = new ProxyInstanceLifetime(directory, builder, proxyProbe, settings, markerOwnerSearch);

            return await proxyInstanceLifetime.AcquireAndStartProxyInstance(arguments, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ValidationException or ArgumentException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"ai-proxy: {exception.Message}");
            return 1;
        }
    }

    private static string EnsureDataDirectory(ProxySettings settings)
    {
        var directory = Path.GetFullPath(settings.DataDirectory ?? ProxyRuntimeOptions.DefaultDataDirectory);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
