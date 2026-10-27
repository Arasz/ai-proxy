using System.Net;
using AiProxy.Hosting;
using AiProxy.Hosting.Marker;
using AiProxy.Tests.Integration;

namespace AiProxy.Tests.Unit;

public sealed class GuardCommandTests
{
    [Fact]
    public async Task A_parent_that_died_with_a_marker_is_cleared_under_the_lock_and_the_marker_is_deleted()
    {
        using var directory = new TemporaryDirectory();
        await LeaveMarker(directory);
        var clears = 0;
        var lockedDuringClear = false;
        var command = new GuardCommand(directory.Path, clearProxy: async () =>
        {
            clears++;
            lockedDuringClear = await ThrowsAsync(() => Task.FromResult(new FileStream(MarkerPath(directory), FileMode.Open, FileAccess.ReadWrite, FileShare.None)));
        }, TimeSpan.Zero, TimeProvider.System);

        var exitCode = await command.Run(new StringReader(""));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, clears);
        Assert.True(lockedDuringClear);
        Assert.False(File.Exists(MarkerPath(directory)));
    }

    [Fact]
    public async Task A_marker_that_another_instance_still_holds_is_left_alone_and_nothing_is_cleared()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using var owner = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System);
        owner.Write(new IPEndPoint(IPAddress.Loopback, ProxyRuntimeOptions.DefaultPort));
        var clears = 0;

        var exitCode = await new GuardCommand(directory.Path, () =>
        {
            try
            {
                clears++;
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }, TimeSpan.FromMilliseconds(100), TimeProvider.System).Run(new StringReader(""));

        Assert.Equal(0, exitCode);
        Assert.Equal(0, clears);
    }

    [Fact]
    public async Task A_marker_that_was_already_truncated_is_not_a_reason_to_clear()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(MarkerPath(directory), "", TestContext.Current.CancellationToken);
        var clears = 0;

        var exitCode = await new GuardCommand(directory.Path, () =>
        {
            try
            {
                clears++;
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }, TimeSpan.Zero, TimeProvider.System).Run(new StringReader(""));

        Assert.Equal(0, exitCode);
        Assert.Equal(0, clears);
        Assert.False(File.Exists(MarkerPath(directory)));
    }

    [Fact]
    public async Task A_parent_that_restores_leaves_both_the_proxy_and_the_marker_alone()
    {
        using var directory = new TemporaryDirectory();
        await LeaveMarker(directory);
        var clears = 0;

        await new GuardCommand(directory.Path, () =>
        {
            try
            {
                clears++;
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }, TimeSpan.Zero, TimeProvider.System).Run(new StringReader("restored\n"));

        Assert.Equal(0, clears);
        Assert.True(File.Exists(MarkerPath(directory)));
    }

    [Fact]
    public async Task A_clear_that_fails_keeps_the_marker_and_returns_an_error_code()
    {
        using var directory = new TemporaryDirectory();
        await LeaveMarker(directory);
        var command = new GuardCommand(directory.Path, static () => {
            try
            {
                throw new InvalidOperationException("networksetup failed");
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }, TimeSpan.Zero, TimeProvider.System);

        var exitCode = await command.Run(new StringReader(""));

        Assert.Equal(1, exitCode);
        Assert.True(new FileInfo(MarkerPath(directory)).Length > 0);
    }

    [Theory]
    [InlineData("--guard-system-proxy", "/data", true)]
    [InlineData("--guard-system-proxy", null, false)]
    [InlineData("--urls", "/data", false)]
    public void Guard_mode_is_requested_only_by_its_argument_and_a_data_directory(string first, string? second, bool expected)
    {
        string[] args = second is null ? [first] : [first, second];

        Assert.Equal(expected, GuardCommand.IsRequested(args));
    }

    private static string MarkerPath(TemporaryDirectory directory)
    {
        return Path.Combine(directory.Path, SystemProxyMarker.FileName);
    }

    private static async Task LeaveMarker(TemporaryDirectory directory)
    {
        Directory.CreateDirectory(directory.Path);
        using var marker = await SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero, TimeProvider.System);
        marker.Write(new IPEndPoint(IPAddress.Loopback, ProxyRuntimeOptions.DefaultPort));
    }

    private static async Task<bool> ThrowsAsync(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
