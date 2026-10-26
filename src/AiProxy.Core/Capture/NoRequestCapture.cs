namespace AiProxy.Core.Capture;

/// <summary>The capture store when capture is off: it keeps nothing.</summary>
public sealed class NoRequestCapture : IRequestCaptureStore
{
    /// <inheritdoc />
    public bool Keeps => false;

    /// <inheritdoc />
    public ValueTask StoreAsync(CapturedRequest request, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StoreResponseAsync(CapturedResponse response, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}