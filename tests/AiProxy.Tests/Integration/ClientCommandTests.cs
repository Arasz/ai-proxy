using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AiProxy.Core.Gain;
using AiProxy.Hosting;
using AiProxy.Hosting.Proxy;

namespace AiProxy.Tests.Integration;

/// <summary>The one ai-proxy tool also carries the gain CLI and the retrieval MCP server, and neither starts the proxy.</summary>
public sealed class ClientCommandTests
{
    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public async Task Root_help_does_not_validate_or_start_the_server(string option)
    {
        var result = await RunAsync([option], new Dictionary<string, string?> { ["Proxy__Port"] = "-1" });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("harness", result.Output, StringComparison.Ordinal);
        Assert.Contains("gain", result.Output, StringComparison.Ordinal);
        Assert.Contains("mcp", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Starting with profile", result.Error + result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gain_runs_the_gain_command_instead_of_the_proxy()
    {
        var result = await RunAsync("gain", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ai-proxy gain", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_with_an_unknown_option_is_rejected_by_spectre_instead_of_starting_the_proxy()
    {
        var result = await RunAsync("mcp", "--port", "1");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unknown option 'port'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Harness_help_does_not_start_the_proxy_or_the_agent()
    {
        var result = await RunAsync("harness", "--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ai-proxy harness", result.Output, StringComparison.Ordinal);
        Assert.Contains("<EXECUTABLE>", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "--model", "literal $(whoami); argument", "", "space value", "--", "--help", "--proxy", "")]
    [InlineData("", "", "")]
    public async Task Harness_inherits_the_console_environment_arguments_and_exit_status(params string[] arguments)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake executable uses a POSIX shell.");
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var executable = Path.Combine(directory.Path, "claude");
        await File.WriteAllTextAsync(executable,
            "#!/bin/sh\nprintf 'HTTP_PROXY=%s\\nHTTPS_PROXY=%s\\nhttp_proxy=%s\\nhttps_proxy=%s\\n' \"$HTTP_PROXY\" \"$HTTPS_PROXY\" \"$http_proxy\" \"$https_proxy\"\nprintf '%s\\n' \"$NODE_EXTRA_CA_CERTS\" \"$ANTHROPIC_CUSTOM_HEADERS\" \"$NO_PROXY\"\nprintf 'arg:<%s>\\n' \"$@\"\nread input\nprintf 'stdin:%s\\n' \"$input\"\nprintf 'child stderr\\n' >&2\nexit 17\n",
            TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var ca = Path.Combine(directory.Path, "root ca.pem");
        await File.WriteAllTextAsync(ca, "public CA fixture", TestContext.Current.CancellationToken);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = directory.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["ANTHROPIC_CUSTOM_HEADERS"] = "Other: preserved\nx-ai-proxy-project: stale",
            ["NO_PROXY"] = "example.test",
            ["no_proxy"] = null,
            ["Proxy__Port"] = port.ToString(CultureInfo.InvariantCulture)
        };
        var result = await RunAsync(["harness", "--ca-file", ca, "claude", "--", .. arguments], environment);
        Assert.Equal(17, result.ExitCode);
        Assert.Contains("stdin:fixture input", result.Output, StringComparison.Ordinal);
        Assert.Contains("child stderr", result.Error, StringComparison.Ordinal);
        foreach (var variable in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
        {
            Assert.Contains($"{variable}={ProjectCredential.ProxyUrl(new Uri($"http://127.0.0.1:{port}"), Environment.CurrentDirectory)}\n", result.Output, StringComparison.Ordinal);
        }

        Assert.Contains(ca, result.Output, StringComparison.Ordinal);
        Assert.Contains("Other: preserved", result.Output, StringComparison.Ordinal);
        Assert.Contains($"X-Ai-Proxy-Project: {Environment.CurrentDirectory}", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("stale", result.Output, StringComparison.Ordinal);
        Assert.Contains("example.test,localhost,127.0.0.1,::1,169.254.169.254,169.254.0.0/16\n", result.Output, StringComparison.Ordinal);
        Assert.Equal(arguments.Select(static argument => $"arg:<{argument}>"),
            result.Output.Split('\n').Where(static line => line.StartsWith("arg:<", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("--ca-file", "__ai_proxy_missing_ca__.pem", "Root CA file is missing")]
    [InlineData("--proxy", "https://localhost:ProxyRuntimeOptions.DefaultPort", "must be an HTTP proxy URL")]
    [InlineData("--proxy", "http://name:secret@localhost:ProxyRuntimeOptions.DefaultPort", "must be an HTTP proxy URL")]
    public async Task Harness_reports_invalid_configuration_without_launching(string option, string value, string message)
    {
        var result = await RunAsync("harness", "claude", option, value);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(message, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_explicit_proxy_overrides_an_invalid_environment_default()
    {
        var result = await RunAsync(["harness", "--proxy", $"http://127.0.0.1:{ProxyRuntimeOptions.DefaultPort}", "--ca-file", "__ai_proxy_missing_ca__.pem", "claude"],
            new Dictionary<string, string?> { ["Proxy__Port"] = "invalid" });
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Root CA file is missing", result.Error, StringComparison.Ordinal);
    }

    private const string EnvironmentReport =
        "#!/bin/sh\nprintf 'args:%s\\n' \"$*\"\nprintf 'CODEX_CA_CERTIFICATE=%s\\nAI_PROXY_CODEX_PROJECT=%s\\nSSL_CERT_FILE=%s\\nNODE_EXTRA_CA_CERTS=%s\\nHTTPS_PROXY=%s\\n' \"$CODEX_CA_CERTIFICATE\" \"$AI_PROXY_CODEX_PROJECT\" \"$SSL_CERT_FILE\" \"$NODE_EXTRA_CA_CERTS\" \"$HTTPS_PROXY\"\n";

    [Fact]
    public async Task Harness_launches_codex_without_a_daemon_and_trusts_the_proxy_root()
    {
        var run = await RunFakeAsync("codex", ["exec", "hi"], new Dictionary<string, string?>
        {
            ["CODEX_CA_CERTIFICATE"] = "/somewhere/else.pem",
            ["SSL_CERT_FILE"] = null
        });
        Assert.Equal(0, run.Result.ExitCode);
        Assert.Contains("args:--no-daemon exec hi\n", run.Result.Output, StringComparison.Ordinal);
        Assert.Contains($"CODEX_CA_CERTIFICATE={run.Ca}\n", run.Result.Output, StringComparison.Ordinal);
        Assert.Contains($"AI_PROXY_CODEX_PROJECT={Environment.CurrentDirectory}\n", run.Result.Output, StringComparison.Ordinal);
        Assert.Contains("SSL_CERT_FILE=\n", run.Result.Output, StringComparison.Ordinal);
        Assert.Contains($"HTTPS_PROXY={ProjectCredential.ProxyUrl(new Uri($"http://127.0.0.1:{run.Port}"), Environment.CurrentDirectory)}\n", run.Result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Harness_leaves_a_preset_ssl_cert_file_untouched_for_codex()
    {
        var run = await RunFakeAsync("codex", ["exec"], new Dictionary<string, string?> { ["SSL_CERT_FILE"] = "/user/bundle.pem" });
        Assert.Contains("SSL_CERT_FILE=/user/bundle.pem\n", run.Result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Harness_gives_other_agents_no_codex_settings()
    {
        var run = await RunFakeAsync("claude", ["exec", "hi"], new Dictionary<string, string?> { ["CODEX_CA_CERTIFICATE"] = null, ["AI_PROXY_CODEX_PROJECT"] = null });
        Assert.Contains("args:exec hi\n", run.Result.Output, StringComparison.Ordinal);
        Assert.Contains("CODEX_CA_CERTIFICATE=\n", run.Result.Output, StringComparison.Ordinal);
        Assert.Contains("AI_PROXY_CODEX_PROJECT=\n", run.Result.Output, StringComparison.Ordinal);
        Assert.Contains($"NODE_EXTRA_CA_CERTS={run.Ca}\n", run.Result.Output, StringComparison.Ordinal);
        Assert.Contains($"HTTPS_PROXY={ProjectCredential.ProxyUrl(new Uri($"http://127.0.0.1:{run.Port}"), Environment.CurrentDirectory)}\n", run.Result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Harness_proxy_credential_decodes_to_the_working_directory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake executable uses a POSIX shell.");
        using var root = new TemporaryDirectory();
        var working = Path.Combine(root.Path, "a b", "ż");
        Directory.CreateDirectory(working);

        var run = await RunFakeAsync("claude", ["x"], new Dictionary<string, string?>(), working);

        var line = run.Result.Output.Split('\n').Single(static line => line.StartsWith("HTTPS_PROXY=", StringComparison.Ordinal));
        var userInfo = Uri.UnescapeDataString(new Uri(line["HTTPS_PROXY=".Length..]).UserInfo);
        Assert.StartsWith("ai-proxy:/", userInfo, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("a b", "ż"), userInfo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Harness_arguments_need_no_separator()
    {
        var run = await RunFakeAsync("claude", ["--resume", "abc"], new Dictionary<string, string?>(), separator: false);

        Assert.Equal(0, run.Result.ExitCode);
        Assert.Contains("args:--resume abc\n", run.Result.Output, StringComparison.Ordinal);
    }

    private static async Task<FakeRun> RunFakeAsync(string name, string[] arguments, Dictionary<string, string?> extraEnvironment, string? workingDirectory = null, bool separator = true)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake executable uses a POSIX shell.");
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var executable = Path.Combine(directory.Path, name);
        await File.WriteAllTextAsync(executable, EnvironmentReport, TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var ca = Path.Combine(directory.Path, "root-ca.pem");
        await File.WriteAllTextAsync(ca, "public CA fixture", TestContext.Current.CancellationToken);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var environment = new Dictionary<string, string?>(extraEnvironment)
        {
            ["PATH"] = directory.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["Proxy__Port"] = port.ToString(CultureInfo.InvariantCulture)
        };
        var result = await RunAsync(["harness", "--ca-file", ca, name, .. separator ? ["--"] : Array.Empty<string>(), .. arguments], environment, workingDirectory);
        return new FakeRun(result, ca, port);
    }

    private sealed record FakeRun(ProcessResult Result, string Ca, int Port);

    private static Task<ProcessResult> RunAsync(params string[] arguments)
    {
        return RunAsync(arguments, new Dictionary<string, string?>());
    }

    private static async Task<ProcessResult> RunAsync(string[] arguments, IReadOnlyDictionary<string, string?> environment, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = workingDirectory ?? "", RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        start.Environment["Proxy__Port"] = "0";
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AiProxy.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await process.StandardInput.WriteLineAsync("fixture input");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, Plain(await output), Plain(await error));
    }

    /// <summary>The text without ANSI styling, which a terminal or CI log detection may add.</summary>
    private static string Plain(string text)
    {
        return Regex.Replace(text, "\u001b\\[[0-9;]*m", "");
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
