namespace AiProxy.Hosting.Proxy;

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
