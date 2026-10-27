namespace AiProxy.Hosting;

/// <summary>Watches the parent's pipe and clears the system proxy when the parent ends without reporting a restore.</summary>
public sealed class SystemProxyGuard(Func<Task> clearProxy)
{
    /// <summary>The line the parent writes after it has restored the user's own proxy settings.</summary>
    public const string RestoredLine = "restored";

    /// <summary>Reads until the end of input; returns true when it cleared the proxy, false when the parent had restored it.</summary>
    public async Task<bool> Run(TextReader input)
    {
        var restored = false;
        while (await input.ReadLineAsync() is { } line)
        {
            restored |= line == RestoredLine;
        }

        if (restored)
        {
            return false;
        }

        await clearProxy();
        return true;
    }
}
