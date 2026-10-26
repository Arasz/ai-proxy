using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using AiProxy.Hosting;

namespace AiProxy;

/// <summary>Launches Claude Code with the running proxy's environment and the current project header.</summary>
internal sealed class ClaudeProcess
{
    public const string Command = "claude";

    public async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is ["--help" or "-h"])
        {
            Console.WriteLine("Usage: ai-proxy claude [--proxy URL] [--ca-file PATH] [--] [claude arguments]\n\nUses an already-running proxy. Defaults: loopback, PROXY__PORT (or 8080), ~/.ai-proxy/root-ca.pem.\nUse -- before Claude arguments that overlap with wrapper options, including --help.");
            return 0;
        }

        try
        {
            string? proxy = null;
            var ca = Path.Combine(ProxyRuntimeOptions.DefaultDataDirectory, TitaniumProxyService.RootCertificatePemFileName);
            var index = 0;
            while (index < arguments.Length)
            {
                var option = arguments[index];
                if (option == "--")
                {
                    index++;
                    break;
                }
                if (option is not ("--proxy" or "--ca-file"))
                {
                    break;
                }
                if (++index == arguments.Length)
                {
                    throw new ArgumentException($"{option} requires a value.");
                }
                if (option == "--proxy")
                {
                    proxy = arguments[index++];
                }
                else
                {
                    ca = arguments[index++];
                }
            }

            if (proxy is null)
            {
                var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
                var port = configuration.GetValue<int?>("Proxy:Port") ?? ProxyRuntimeOptions.Default.Port;
                proxy = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
            }
            if (!Uri.TryCreate(proxy, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp
                || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            {
                throw new ArgumentException("--proxy must be an HTTP proxy URL without credentials, path, query or fragment.");
            }
            ca = Path.GetFullPath(ca);
            if (!File.Exists(ca))
            {
                throw new ArgumentException("Root CA file is missing. Start ai-proxy once to create ~/.ai-proxy/root-ca.pem, or pass --ca-file PATH.");
            }
            var project = Environment.CurrentDirectory;
            if (project.IndexOfAny(['\r', '\n']) >= 0)
            {
                throw new ArgumentException("The project directory cannot contain a newline in an HTTP header.");
            }
            using (var connection = new TcpClient())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    await connection.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                {
                    throw new ArgumentException("The proxy is unreachable. Start ai-proxy in another terminal, or pass --proxy URL.");
                }
            }

            var start = new ProcessStartInfo("claude") { UseShellExecute = false };
            foreach (var argument in arguments[index..])
            {
                start.ArgumentList.Add(argument);
            }
            foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
            {
                start.Environment[key] = endpoint.GetLeftPart(UriPartial.Authority);
            }
            start.Environment.TryGetValue("NO_PROXY", out var upperBypass);
            start.Environment.TryGetValue("no_proxy", out var lowerBypass);
            start.Environment.TryGetValue("ANTHROPIC_CUSTOM_HEADERS", out var customHeaders);
            var bypass = string.Join(',', new[] { upperBypass, lowerBypass, "localhost,127.0.0.1,::1" }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            start.Environment["NO_PROXY"] = bypass;
            start.Environment["no_proxy"] = bypass;
            start.Environment["NODE_EXTRA_CA_CERTS"] = ca;
            var headers = (customHeaders ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(header => !header.Split(':', 2)[0].Trim().Equals("X-Ai-Proxy-Project", StringComparison.OrdinalIgnoreCase));
            start.Environment["ANTHROPIC_CUSTOM_HEADERS"] = string.Join('\n', headers.Append($"X-Ai-Proxy-Project: {project}"));

            ConsoleCancelEventHandler interrupt = (_, e) => e.Cancel = true;
            Console.CancelKeyPress += interrupt;
            try
            {
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Claude Code did not start.");
                await child.WaitForExitAsync();
                return child.ExitCode;
            }
            finally
            {
                Console.CancelKeyPress -= interrupt;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or Win32Exception or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync($"ai-proxy claude: {exception.Message}");
            return 1;
        }
    }
}
