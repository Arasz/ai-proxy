using System.Diagnostics;
using System.Runtime.Versioning;
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
    public async Task The_shell_shortcut_passes_every_argument_to_ai_proxy_harness()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Runs the POSIX script.");
        WriteTool("#!/bin/sh\nfor argument in \"$@\"; do printf '<%s>\\n' \"$argument\"; done\n");

        Assert.Equal(0, await RunAsync("shortcut"));

        var start = new ProcessStartInfo(Aph) { RedirectStandardOutput = true };
        foreach (var argument in new[] { "claude", "a b", "$HOME", "--", "", "q\"x'y" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("<harness>\n<claude>\n<a b>\n<$HOME>\n<-->\n<>\n<q\"x'y>\n", output);
    }

    [Fact]
    public async Task The_batch_shortcut_is_ascii_and_finds_the_tool_beside_itself()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory.Path, "Rafa\u0142 100%")).FullName;
        var shortcut = new HarnessShortcut(Path.Combine(directory, "ai-proxy.exe"), ShortcutScript.Batch);

        shortcut.Create();

        var text = await File.ReadAllTextAsync(shortcut.FilePath, TestContext.Current.CancellationToken);
        Assert.Contains("\"%~dp0ai-proxy.exe\" harness %*", text, StringComparison.Ordinal);
        Assert.All(text, static character => Assert.True(char.IsAscii(character)));
        Assert.Equal("aph.cmd", Path.GetFileName(shortcut.FilePath));
    }

    [Fact]
    public async Task A_second_run_rewrites_a_stale_shortcut_and_remove_deletes_it()
    {
        WriteTool();
        await RunAsync("shortcut");
        var fresh = await File.ReadAllTextAsync(Aph, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Aph, fresh.Replace("ai-proxy'", "old-path'", StringComparison.Ordinal).Replace("ai-proxy.exe", "old-path", StringComparison.Ordinal), TestContext.Current.CancellationToken);

        Assert.Equal(0, await RunAsync("shortcut"));
        Assert.Equal(fresh, await File.ReadAllTextAsync(Aph, TestContext.Current.CancellationToken));

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
    public async Task A_file_that_only_mentions_the_marker_is_not_ours()
    {
        WriteTool();
        const string wrapper = "#!/bin/sh\necho copied from: Created by ai-proxy shortcut.\n";
        await File.WriteAllTextAsync(Aph, wrapper, TestContext.Current.CancellationToken);

        Assert.Equal(1, await RunAsync("shortcut"));

        Assert.Equal(wrapper, await File.ReadAllTextAsync(Aph, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_symlink_at_the_shortcut_path_is_not_written_through()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symlink needs a privilege on Windows.");
        WriteTool();
        var target = Path.Combine(_directory.Path, "target");
        await File.WriteAllTextAsync(target, "x\n# Created by ai-proxy shortcut.\n", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Aph, target);

        Assert.Equal(1, await RunAsync("shortcut"));

        Assert.Equal("x\n# Created by ai-proxy shortcut.\n", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_directory_that_cannot_be_written_exits_1_with_a_message()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses POSIX directory modes.");
        WriteTool();
        File.SetUnixFileMode(_directory.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(1, await RunAsync("shortcut"));
        }
        finally
        {
            File.SetUnixFileMode(_directory.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Contains("could not be changed", _fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task An_ai_proxy_that_is_not_executable_is_skipped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no execute bit.");
        WriteTool();
        File.SetUnixFileMode(ToolPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Equal(1, await RunAsync("shortcut"));

        Assert.False(File.Exists(Aph));
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

        new HarnessShortcut(tool, ShortcutScript.Shell).Create();

        Assert.Contains(@"it'\''s", File.ReadAllText(Path.Combine(directory, "aph")), StringComparison.Ordinal);
    }

    private string ToolPath => Path.GetFullPath(Path.Combine(_directory.Path, OperatingSystem.IsWindows() ? "ai-proxy.exe" : "ai-proxy"));

    private void WriteTool(string content = "")
    {
        Directory.CreateDirectory(_directory.Path);
        File.WriteAllText(ToolPath, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ToolPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private Task<int> RunAsync(params string[] arguments)
    {
        var runtime = _fixture.Runtime with { Variable = name => name == "PATH" ? _directory.Path : null };
        return new CliApplication(runtime).RunAsync(arguments, TestContext.Current.CancellationToken);
    }
}
