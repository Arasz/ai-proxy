using AiProxy.Hosting.Proxy;

namespace AiProxy.Tests.Integration;

public sealed class ProxyLogFileChildTests
{
    [Fact]
    public async Task A_started_proxy_writes_its_listening_event_to_proxy_log_and_still_to_the_console()
    {
        using var directory = new TemporaryDirectory();
        await using var child = ProxyChild.Start(directory.Path, restart: false);
        await child.ReadyAsync();

        var text = await ReadWhenContainsAsync(Path.Combine(directory.Path, ProxyLogFile.FileName), " 1301 ");

        Assert.Contains(" 1301 ", text, StringComparison.Ordinal);
        Assert.Contains("[1301]", child.Output, StringComparison.Ordinal);
    }

    private static async Task<string> ReadWhenContainsAsync(string path, string needle)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var text = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = await reader.ReadToEndAsync();
                if (text.Contains(needle, StringComparison.Ordinal))
                {
                    return text;
                }
            }

            await Task.Delay(100);
        }

        return text;
    }
}
