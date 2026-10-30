using Microsoft.Extensions.Time.Testing;

namespace AiProxy.Tests;

/// <summary>A fake clock that reports when code under test first waits on it, so a test can act once the wait has begun.</summary>
public sealed class TimerObservingTimeProvider : FakeTimeProvider
{
    private readonly TaskCompletionSource _firstTimer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the first timer (a <c>Task.Delay</c> on this clock, for instance) is created.</summary>
    public Task FirstTimerCreated => _firstTimer.Task;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _firstTimer.TrySetResult();
        return timer;
    }
}
