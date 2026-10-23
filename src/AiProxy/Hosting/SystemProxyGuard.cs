namespace AiProxy.Hosting;

/// <summary>Watches the parent's pipe and clears the system proxy when the parent ends without reporting a restore.</summary>
public sealed class SystemProxyGuard
{
    /// <summary>The line the parent writes after it has restored the user's own proxy settings.</summary>
    public const string RestoredLine = "restored";

    private readonly Action _clear;

    /// <summary>Creates a guard that calls <paramref name="clear"/> when the parent died.</summary>
    public SystemProxyGuard(Action clear)
    {
        ArgumentNullException.ThrowIfNull(clear);
        _clear = clear;
    }

    /// <summary>Reads until end of input; returns true when it cleared the proxy, false when the parent had restored it.</summary>
    public bool Run(TextReader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var restored = false;
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            restored |= line == RestoredLine;
        }

        if (restored)
        {
            return false;
        }

        _clear();
        return true;
    }
}
