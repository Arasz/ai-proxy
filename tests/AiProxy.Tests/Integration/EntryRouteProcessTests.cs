using System.Net;
using System.Net.Sockets;
using AiProxy.Cli;
using AiProxy.Tests.Unit.Cli;

namespace AiProxy.Tests.Integration;

/// <summary>Real child processes on a throwaway home and an unused port: a rejected command line must exit before anything starts.</summary>
public sealed class EntryRouteProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ai-proxy-entry-route-{Guid.NewGuid():N}");

    public EntryRouteProcessTests()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(WorkingDirectory);
    }

    private string DataDirectory => Path.Combine(_root, "data");

    private string WorkingDirectory => Path.Combine(_root, "cwd");

    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("stauts")]
    [InlineData("bogus")]
    [InlineData("--Proxy:Port")]
    public async Task Rejected_start_line_prints_usage_with_the_parse_error_code_and_starts_nothing(string badToken)
    {
        var expected = await ParseErrorCodeAsync(badToken);
        Assert.NotEqual(0, expected);

        await using var child = Start([badToken]);
        await child.ExitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(expected, child.Process.ExitCode);
        Assert.Contains("Start: ai-proxy", child.Output, StringComparison.Ordinal);
        Assert.Contains(badToken, child.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", child.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(DataDirectory));
        Assert.False(Directory.Exists(Path.Combine(child.Home, ".ai-proxy")));
    }

    [Theory]
    [InlineData("stop", "--data-directory")]
    [InlineData("gain", "--rule")]
    [InlineData("gain", "--provider")]
    [InlineData("replay", "--home")]
    public async Task Option_with_no_value_is_rejected_by_name_and_binds_no_folder(string command, string option)
    {
        string[] arguments = command == "gain" ? [command, "--endpoint", $"http://127.0.0.1:{FreePort()}", option] : [command, option];
        await using var child = Start(arguments);
        await child.ExitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(0, child.Process.ExitCode);
        Assert.Contains(option, child.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(WorkingDirectory, "__default_command")), child.Output);
    }

    private ProxyChild Start(string[] arguments)
    {
        var environment = new Dictionary<string, string?> { ["Proxy__Port"] = FreePort().ToString() };
        return ProxyChild.StartWith(DataDirectory, arguments, false, environment, WorkingDirectory);
    }

    private static async Task<int> ParseErrorCodeAsync(string badToken)
    {
        using var fixture = new GainCommandTests.Fixture();
        return await new CliApplication(fixture.Runtime).RunAsync([badToken], TestContext.Current.CancellationToken);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
