using System.Diagnostics;
using System.Text.Json;
using AiProxy.Hosting.Api;
using AiProxy.Hosting.Proxy;
using AiProxy.Tests.Integration.Mcp;

namespace AiProxy.Tests.Integration;

/// <summary>A real proxy child on a data directory no default resolves to, and the clients that find its token through <c>--data-directory</c>.</summary>
public sealed class ApiTokenProcessTests
{
    [Fact]
    public async Task Each_start_writes_a_new_token_only_the_owner_can_read()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ApiToken.FileName);
        string first;
        await using (var child = ProxyChild.Start(directory.Path, restart: false))
        {
            await child.ReadyAsync();
            first = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        }

        await using (var child = ProxyChild.Start(directory.Path, restart: false))
        {
            await child.ReadyAsync();
            var second = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.NotEqual(first, second);
            Assert.Equal(64, second.Length);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task Gain_finds_the_token_through_data_directory_and_gets_401_without_it()
    {
        using var directory = new TemporaryDirectory();
        await using var child = ProxyChild.Start(directory.Path, restart: false);
        var endpoint = await ApiEndpointAsync(child, directory.Path);

        var found = await RunAsync("gain", "--endpoint", endpoint, "--format", "json", "--data-directory", directory.Path);
        var missing = await RunAsync("gain", "--endpoint", endpoint, "--format", "json");

        Assert.True(found.ExitCode == 0, found.Error);
        Assert.Contains("\"summary\"", found.Output, StringComparison.Ordinal);
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("401", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_retrieval_sends_the_token_from_data_directory_and_gets_401_once_the_file_is_gone()
    {
        using var directory = new TemporaryDirectory();
        await using var child = ProxyChild.StartWith(directory.Path, [], environment: new Dictionary<string, string?>
        {
            ["Proxy__Compression__Enabled"] = "true",
            ["Proxy__Compression__Database"] = Path.Combine(directory.Path, "content.db")
        });
        var endpoint = await ApiEndpointAsync(child, directory.Path);
        await using var server = new McpProcess(endpoint, directory.Path);
        await server.InitializeAsync();

        var withToken = await RetrieveAsync(server);
        File.Delete(Path.Combine(directory.Path, ApiToken.FileName));
        var withoutToken = await RetrieveAsync(server);

        Assert.DoesNotContain("HTTP", withToken, StringComparison.Ordinal);
        Assert.Contains("0123456789abcdef", withToken, StringComparison.Ordinal);
        Assert.Contains("HTTP 401", withoutToken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_without_data_directory_gets_401()
    {
        using var directory = new TemporaryDirectory();
        await using var child = ProxyChild.Start(directory.Path, restart: false);
        var endpoint = await ApiEndpointAsync(child, directory.Path);
        await using var server = new McpProcess(endpoint);
        await server.InitializeAsync();

        Assert.Contains("HTTP 401", await RetrieveAsync(server), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_api_check_passes_only_with_the_token_and_status_needs_none()
    {
        using var directory = new TemporaryDirectory();
        await using var child = ProxyChild.Start(directory.Path, restart: false);
        var endpoint = await ApiEndpointAsync(child, directory.Path);

        var found = await ApiCheckAsync("--endpoint", endpoint, "--data-directory", directory.Path);
        var defaulted = await ApiCheckAsync("--endpoint", endpoint);
        File.Delete(Path.Combine(directory.Path, ApiToken.FileName));
        var deleted = await ApiCheckAsync("--endpoint", endpoint, "--data-directory", directory.Path);
        var status = await RunAsync("status", "--data-directory", directory.Path);

        Assert.Equal("pass", found.GetProperty("status").GetString());
        Assert.Equal("fail", defaulted.GetProperty("status").GetString());
        Assert.Contains("token", defaulted.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal("fail", deleted.GetProperty("status").GetString());
        Assert.True(status.ExitCode == 0, status.Error);
        Assert.Contains(child.Process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), status.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_finds_the_token_from_the_data_directory_environment_variable_alone()
    {
        using var directory = new TemporaryDirectory();
        await using var child = ProxyChild.Start(directory.Path, restart: false);
        var endpoint = await ApiEndpointAsync(child, directory.Path);

        var result = await RunAsync(new Dictionary<string, string> { ["Proxy__DataDirectory"] = directory.Path }, "doctor", "--endpoint", endpoint, "--format", "json", "--timeout", "30");

        using var json = JsonDocument.Parse(result.Output);
        var api = json.RootElement.GetProperty("checks").EnumerateArray().Single(static check => check.GetProperty("name").GetString() == "api");
        Assert.Equal("pass", api.GetProperty("status").GetString());
    }

    private static async Task<string> RetrieveAsync(McpProcess server)
    {
        var response = await server.CallAsync("tools/call", new { name = "retrieve_original", arguments = new { id = "0123456789abcdef", mode = "whole" } });
        return response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static async Task<JsonElement> ApiCheckAsync(params string[] arguments)
    {
        var result = await RunAsync(["doctor", .. arguments, "--format", "json", "--timeout", "30"]);
        using var json = JsonDocument.Parse(result.Output);
        return json.RootElement.GetProperty("checks").EnumerateArray().Single(static check => check.GetProperty("name").GetString() == "api").Clone();
    }

    private static async Task<string> ApiEndpointAsync(ProxyChild child, string directory)
    {
        await child.ReadyAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await ProxyControl.TryReadOwnerAsync(directory, TestContext.Current.CancellationToken) is { } owner)
            {
                return owner.ApiUrls.First();
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(child.Output);
    }

    /// <summary>Runs one client command under a throwaway home and without <c>Proxy__DataDirectory</c>, so no default reaches the child's directory.</summary>
    private static Task<ProcessResult> RunAsync(params string[] arguments)
    {
        return RunAsync(new Dictionary<string, string>(), arguments);
    }

    private static async Task<ProcessResult> RunAsync(IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        using var home = new TemporaryDirectory();
        Directory.CreateDirectory(home.Path);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AiProxy.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["HOME"] = home.Path;
        start.Environment["USERPROFILE"] = home.Path;
        start.Environment.Remove("Proxy__DataDirectory");
        start.Environment.Remove("PROXY__DATADIRECTORY");
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
