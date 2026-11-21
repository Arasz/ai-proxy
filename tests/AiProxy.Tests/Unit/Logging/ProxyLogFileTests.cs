using AiProxy.Hosting.Proxy;
using AiProxy.Tests.Integration;
using Microsoft.Extensions.Logging;

namespace AiProxy.Tests.Unit.Logging;

public sealed class ProxyLogFileTests
{
    [Fact]
    public void A_line_holds_time_level_event_id_category_message_and_exception()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "proxy.log");

        using (var provider = new ProxyLogFile(path, 1024 * 1024))
        {
            provider.CreateLogger("Some.Category").Log(LogLevel.Warning, new EventId(1502), new InvalidOperationException("boom"), "capture stopped");
        }

        var text = File.ReadAllText(path);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z Warning 1502 Some\.Category capture stopped", text);
        Assert.Contains("InvalidOperationException: boom", text, StringComparison.Ordinal);
    }

    [Fact]
    public void On_posix_the_file_is_readable_and_writable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "proxy.log");

        using (var provider = new ProxyLogFile(path, 1024 * 1024))
        {
            provider.CreateLogger("c").LogInformation("hello");
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void At_the_size_limit_the_file_rolls_and_two_files_are_the_whole_history()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "proxy.log");
        const int limit = 600;

        using (var provider = new ProxyLogFile(path, limit))
        {
            var logger = provider.CreateLogger("c");
            for (var index = 0; index < 100; index++)
            {
                logger.LogInformation("line number {Index} with some padding text", index);
            }
        }

        var rolled = Path.Combine(directory.Path, "proxy.1.log");
        Assert.True(File.Exists(rolled));
        Assert.True(new FileInfo(path).Length <= limit);
        Assert.True(new FileInfo(rolled).Length <= limit);
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
        Assert.Contains("line number 99 ", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_later_start_appends_to_the_existing_file()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "proxy.log");

        using (var first = new ProxyLogFile(path, 1024 * 1024))
        {
            first.CreateLogger("c").LogInformation("first start");
        }

        using (var second = new ProxyLogFile(path, 1024 * 1024))
        {
            second.CreateLogger("c").LogInformation("second start");
        }

        var text = File.ReadAllText(path);
        Assert.Contains("first start", text, StringComparison.Ordinal);
        Assert.Contains("second start", text, StringComparison.Ordinal);
    }
}
