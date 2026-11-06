using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AiProxy.Cli;
using AiProxy.Client;
using AiProxy.Core.Doctor;
using AiProxy.Core.Providers;
using AiProxy.Hosting;
using Spectre.Console.Testing;
using RootCertificateAuthority = AiProxy.Hosting.Integration.RootCertificateAuthority;

namespace AiProxy.Tests.Integration;

public sealed class DoctorCommandTests
{
    private const string Host = "api.anthropic.com";

    private sealed class NamedHosts : IProviderHosts
    {
        public ProviderHost? Find(string host) => host == Host ? new ProviderHost(Dialect.Anthropic, HarnessOnly: false) : null;
        public IReadOnlyCollection<string> Hosts { get; } = [Host];
    }

    private sealed record Outcome(int Exit, string Text, string Json, string Error);

    private static async Task<Outcome> RunAsync(Dictionary<string, string> environment, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var console = new TestConsole();
        using var http = new HttpClient();
        var runtime = new GainRuntime(console, output, error, new ProxyClient(http), "/work", false, TimeProvider.System, null, name => environment.GetValueOrDefault(name));
        var exit = await new CliApplication(runtime).RunAsync(["doctor", .. args], TestContext.Current.CancellationToken);
        return new Outcome(exit, console.Output, output.ToString(), error.ToString());
    }

    private static string[] Options(ProxyHost proxy, string? host = null)
    {
        return ["--endpoint", proxy.BaseAddress.ToString(), "--proxy", proxy.ProxyAddress.ToString(), "--data-directory", proxy.DataDirectory, "--host", host ?? "api.anthropic.com", "--timeout", "60"];
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task A_healthy_setup_with_a_current_proxy_reports_json_with_every_check()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        var outcome = await RunAsync([], [.. Options(proxy), "--format", "json"]);

        using var json = JsonDocument.Parse(outcome.Json);
        var names = json.RootElement.GetProperty("checks").EnumerateArray().Select(static check => check.GetProperty("name").GetString()).ToArray();
        Assert.Equal(["api", "inspection", "root", "environment"], names);
        Assert.Equal(outcome.Exit, json.RootElement.GetProperty("exitCode").GetInt32());
        var statuses = json.RootElement.GetProperty("checks").EnumerateArray().Select(static check => check.GetProperty("status").GetString()).ToArray();
        Assert.Equal(["pass", "pass", "warn", "pass"], statuses);
        Assert.Equal(2, outcome.Exit);
        Assert.Equal("", outcome.Error);
    }

    [Fact]
    public async Task Text_output_is_a_table_of_the_checks()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        var outcome = await RunAsync([], Options(proxy));
        Assert.Contains("api", outcome.Text);
        Assert.Contains("environment", outcome.Text);
        Assert.Contains("root", outcome.Text);
    }

    [Fact]
    public async Task A_stopped_proxy_reports_one_not_running_failure_and_skips_inspection_but_still_checks_the_root()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using var root = RootCertificateAuthority.Create(Core.Providers.ProviderHostCatalog.Hosts, DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(directory.Path, "root-ca.pem"), root.ExportCertificatePem());
        var proxyPort = ClosedPort();
        var outcome = await RunAsync([], "--endpoint", $"http://127.0.0.1:{ClosedPort()}", "--proxy", $"http://127.0.0.1:{proxyPort}", "--data-directory", directory.Path, "--format", "json");

        using var json = JsonDocument.Parse(outcome.Json);
        var checks = json.RootElement.GetProperty("checks");
        Assert.Equal("fail", checks[0].GetProperty("status").GetString());
        Assert.Contains("not running", checks[0].GetProperty("detail").GetString());
        Assert.Equal("unknown", checks[1].GetProperty("status").GetString());
        Assert.Contains("skipped", checks[1].GetProperty("detail").GetString());
        Assert.Equal("root", checks[2].GetProperty("name").GetString());
        Assert.Equal(1, outcome.Exit);
    }

    [Fact]
    public async Task An_environment_proxy_secret_never_reaches_text_or_json_output()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        var environment = new Dictionary<string, string> { ["HTTPS_PROXY"] = $"http://u:SENTINEL@127.0.0.1:{ClosedPort()}/?k=SENTINEL2" };
        var text = await RunAsync(environment, Options(proxy));
        var json = await RunAsync(environment, [.. Options(proxy), "--format", "json"]);
        Assert.DoesNotContain("SENTINEL", text.Text + text.Json + text.Error);
        Assert.DoesNotContain("SENTINEL", json.Text + json.Json + json.Error);
        Assert.Equal(1, json.Exit);
    }

    [Fact]
    public async Task No_output_contains_pem_text()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>());
        var outcome = await RunAsync([], [.. Options(proxy), "--format", "json"]);
        Assert.DoesNotContain("-----", outcome.Text + outcome.Json);
    }

    [Fact]
    public async Task A_running_proxy_with_an_unreachable_upstream_does_not_fail_the_inspection_check()
    {
        await using var proxy = await ProxyHost.StartAsync(new Dictionary<string, string>(), providerHosts: new NamedHosts());
        var outcome = await RunAsync([], [.. Options(proxy), "--format", "json"]);
        using var json = JsonDocument.Parse(outcome.Json);
        Assert.Equal("pass", json.RootElement.GetProperty("checks")[1].GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("--host", "example.com", "provider hosts")]
    [InlineData("--host", "chatgpt.com", "provider hosts")]
    [InlineData("--format", "xml", "text or json")]
    [InlineData("--timeout", "0", "between 1 and 300")]
    [InlineData("--endpoint", "not a url", "absolute HTTP")]
    public async Task Bad_options_exit_one_with_a_message_on_stderr(string option, string value, string message)
    {
        var outcome = await RunAsync([], option, value);
        Assert.Equal(1, outcome.Exit);
        Assert.Contains(message, outcome.Error);
    }
}
