using System.Diagnostics;

namespace AiProxy.Tests.Integration;

/// <summary>The one ai-proxy tool also carries the gain CLI and the retrieval MCP server, and neither starts the proxy.</summary>
public sealed class ClientCommandTests
{
    [Fact]
    public async Task Gain_runs_the_gain_command_instead_of_the_proxy()
    {
        var result = await RunAsync("gain", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ai-proxy gain", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_with_an_unknown_option_prints_its_usage_instead_of_starting_the_proxy()
    {
        var result = await RunAsync("mcp", "--port", "1");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage: ai-proxy mcp", result.Error, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AiProxy.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, Plain(await output), Plain(await error));
    }

    /// <summary>The text without ANSI styling, which a terminal or CI log detection may add.</summary>
    private static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, "\u001b\\[[0-9;]*m", string.Empty);

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
