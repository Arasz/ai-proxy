using AiProxy.Cli;
using AiProxy.Tests.Integration;

namespace AiProxy.Tests.Unit.Cli;

public sealed class ShortcutCommandTests : IDisposable
{
    private readonly GainCommandTests.Fixture _fixture = new();
    private readonly TemporaryDirectory _directory = new();

    private string Aph => Path.Combine(_directory.Path, OperatingSystem.IsWindows() ? "aph.cmd" : "aph");

    public void Dispose()
    {
        _directory.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public async Task The_shortcut_is_written_beside_ai_proxy_and_calls_harness()
    {
        WriteTool();

        Assert.Equal(0, await RunAsync("shortcut"));

        var text = await File.ReadAllTextAsync(Aph, TestContext.Current.CancellationToken);
        Assert.Contains(ToolPath, text, StringComparison.Ordinal);
        Assert.Contains("harness", text, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Aph).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public async Task A_second_run_replaces_our_shortcut_and_remove_deletes_it()
    {
        WriteTool();
        await RunAsync("shortcut");

        Assert.Equal(0, await RunAsync("shortcut"));
        Assert.Equal(0, await RunAsync("shortcut", "--remove"));

        Assert.False(File.Exists(Aph));
    }

    [Theory]
    [InlineData("shortcut")]
    [InlineData("shortcut", "--remove")]
    public async Task A_file_we_did_not_create_is_left_unchanged(params string[] arguments)
    {
        WriteTool();
        await File.WriteAllTextAsync(Aph, "someone else's script", TestContext.Current.CancellationToken);

        Assert.Equal(1, await RunAsync(arguments));

        Assert.Equal("someone else's script", await File.ReadAllTextAsync(Aph, TestContext.Current.CancellationToken));
        Assert.Contains("left unchanged", _fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_ai_proxy_on_path_nothing_is_written()
    {
        Assert.Equal(1, await RunAsync("shortcut"));

        Assert.False(File.Exists(Aph));
        Assert.Contains("not found on PATH", _fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_quote_in_the_tool_path_cannot_break_out_of_the_shell_script()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Shell quoting applies to the POSIX script.");
        var directory = Directory.CreateDirectory(Path.Combine(_directory.Path, "it's")).FullName;
        var tool = Path.Combine(directory, "ai-proxy");

        new HarnessShortcut(tool, false).Create();

        Assert.Contains(@"it'\''s", File.ReadAllText(Path.Combine(directory, "aph")), StringComparison.Ordinal);
    }

    private string ToolPath => Path.GetFullPath(Path.Combine(_directory.Path, OperatingSystem.IsWindows() ? "ai-proxy.exe" : "ai-proxy"));

    private void WriteTool()
    {
        Directory.CreateDirectory(_directory.Path);
        File.WriteAllText(ToolPath, "");
    }

    private Task<int> RunAsync(params string[] arguments)
    {
        var runtime = _fixture.Runtime with { Variable = name => name == "PATH" ? _directory.Path : null };
        return new CliApplication(runtime).RunAsync(arguments, TestContext.Current.CancellationToken);
    }
}
