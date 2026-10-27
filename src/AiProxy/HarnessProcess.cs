using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using AiProxy.Core.Gain;
using AiProxy.Hosting;
using JetBrains.Annotations;
using Spectre.Console.Cli;

namespace AiProxy;

/// <summary>Launches a command-line harness through the running proxy.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class HarnessProcess : AsyncCommand<HarnessProcess.Settings>
{
    public const string Command = CommandLine.Name;

    private static readonly char[] NewLineChars = ['\r', '\n'];

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        static void OnCancel(object? _, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
        }

        try
        {
            var proxy = settings.Proxy;
            if (proxy is null)
            {
                var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
                var port = configuration.GetValue<int?>(KnownNames.Configuration.ProxyPort) ?? ProxyRuntimeOptions.Default.Port;
                proxy = string.Format(CultureInfo.InvariantCulture, KnownNames.Network.ProxyUrlFormat, port);
            }

            if (!Uri.TryCreate(proxy, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp
                                                                          || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != KnownNames.Network.RootPath || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            {
                throw new ArgumentException(Messages.InvalidProxy);
            }

            var ca = GetRootCa(settings);
            var project = GetProjectDirectory();
            using (var connection = new TcpClient())
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await connection.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                {
                    throw new ArgumentException(Messages.UnreachableProxy);
                }
            }

            var start = new ProcessStartInfo(settings.Executable) { UseShellExecute = false };
            IEnumerable<string> arguments = context.Data is string[] forwardedArguments ? forwardedArguments : context.Remaining.Raw;
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            foreach (var key in new[] { KnownNames.EnvironmentVariables.HttpProxy, KnownNames.EnvironmentVariables.HttpsProxy, KnownNames.EnvironmentVariables.LowerHttpProxy, KnownNames.EnvironmentVariables.LowerHttpsProxy })
            {
                start.Environment[key] = endpoint.GetLeftPart(UriPartial.Authority);
            }

            start.Environment.TryGetValue(KnownNames.EnvironmentVariables.NoProxy, out var upperBypass);
            start.Environment.TryGetValue(KnownNames.EnvironmentVariables.LowerNoProxy, out var lowerBypass);
            start.Environment.TryGetValue(KnownNames.EnvironmentVariables.AnthropicCustomHeaders, out var customHeaders);
            var bypass = string.Join(',', new[] { upperBypass, lowerBypass, KnownNames.Network.LoopbackBypass }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
            start.Environment[KnownNames.EnvironmentVariables.NoProxy] = bypass;
            start.Environment[KnownNames.EnvironmentVariables.LowerNoProxy] = bypass;
            start.Environment[KnownNames.EnvironmentVariables.NodeExtraCaCerts] = ca;
            var headers = (customHeaders ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(static header => !header.Split(':', 2)[0].Trim().Equals(GainApi.ProjectHeader, StringComparison.OrdinalIgnoreCase))
                .ToHashSet();
            start.Environment[KnownNames.EnvironmentVariables.AnthropicCustomHeaders] = string.Join('\n', headers.Append(KnownNames.Headers.ProjectPrefix + project));

            Console.CancelKeyPress += OnCancel;
            try
            {
                using var child = Process.Start(start) ?? throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, Messages.HarnessNotStartedFormat, settings.Executable));
                await child.WaitForExitAsync(cancellationToken);
                return child.ExitCode;
            }
            finally
            {
                Console.CancelKeyPress -= OnCancel;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or Win32Exception or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(CommandLine.ErrorPrefix + exception.Message);
            return 1;
        }
    }

    private static string GetProjectDirectory()
    {
        var project = Environment.CurrentDirectory;
        return project.IndexOfAny(NewLineChars) >= 0 ? throw new ArgumentException(Messages.InvalidProjectDirectory) : project;
    }

    private static string GetRootCa(Settings settings)
    {
        var ca = Path.GetFullPath(settings.CaFile ?? Path.Combine(ProxyRuntimeOptions.DefaultDataDirectory, TitaniumProxyService.RootCertificatePemFileName));
        return !File.Exists(ca) ? throw new ArgumentException(Messages.MissingRootCa) : ca;
    }

    private static class Messages
    {
        public const string InvalidProxy = "--proxy must be an HTTP proxy URL without credentials, path, query or fragment.";
        public const string UnreachableProxy = "The proxy is unreachable. Start ai-proxy in another terminal, or pass --proxy URL.";
        public const string InvalidProjectDirectory = "The project directory cannot contain a newline in an HTTP header.";
        public const string MissingRootCa = "Root CA file is missing. Start ai-proxy once to create ~/.ai-proxy/root-ca.pem, or pass --ca-file PATH.";
        public const string HarnessNotStartedFormat = "Harness '{0}' did not start.";
    }

    private static class CommandLine
    {
        public const string Name = "harness";
        public const string ErrorPrefix = $"ai-proxy {Name}: ";
        public const string ExecutableArgument = "<EXECUTABLE>";
        public const string ExecutableDescription = "Harness executable to launch.";
        public const string ProxyOption = "--proxy <URL>";
        public const string ProxyDescription = "HTTP proxy URL (defaults to loopback and Proxy:Port or default port).";
        public const string CaFileOption = "--ca-file <PATH>";
        public const string CaFileDescription = "Root CA PEM file (defaults to ~/.ai-proxy/root-ca.pem).";
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, CommandLine.ExecutableArgument)]
        [Description(CommandLine.ExecutableDescription)]
        public string Executable { get; init; } = "";

        [CommandOption(CommandLine.ProxyOption)]
        [Description(CommandLine.ProxyDescription)]
        public string? Proxy { get; init; }

        [CommandOption(CommandLine.CaFileOption)]
        [Description(CommandLine.CaFileDescription)]
        public string? CaFile { get; init; }
    }
}
