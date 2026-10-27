namespace AiProxy.Hosting;

public sealed record StartupLock(Stream File): IAsyncDisposable
{
    public static readonly StartupLock NoOpLock = new(Stream.Null);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => File.DisposeAsync();
}
