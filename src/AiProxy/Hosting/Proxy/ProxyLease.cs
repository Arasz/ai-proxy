namespace AiProxy.Hosting.Proxy;

/// <summary>Takes the startup and runtime lock files that keep one proxy instance running per data directory.</summary>
public sealed class ProxyLease
{
    private const string StartupLeaseFileName = "startup.lock";
    private const string LeaseFileName = "process.lock";
    public StartupLease AcquireStartupLease(string dir)
    {
        Directory.CreateDirectory(dir);
        var startupLeaseFileName = Path.Combine(dir, StartupLeaseFileName);
        var startupLeaseFile = new FileStream(startupLeaseFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return new StartupLease(startupLeaseFile);
    }

    public RuntimeLease AcquireRuntimeLease(string dir)
    {
        Directory.CreateDirectory(dir);
        var runtimeLeaseFileName = Path.Combine(dir, LeaseFileName);
        var runtimeLeaseFile = new FileStream(runtimeLeaseFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return new RuntimeLease(runtimeLeaseFile);
    }
}

public sealed record StartupLease(Stream File) : IAsyncDisposable
{
    public static readonly StartupLease NoOpLease = new(Stream.Null);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => File.DisposeAsync();
}

public sealed record RuntimeLease(Stream File) : IAsyncDisposable
{
    public static readonly RuntimeLease NoOpLease = new(Stream.Null);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => File.DisposeAsync();
}
