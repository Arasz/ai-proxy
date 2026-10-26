using System.Collections.Concurrent;
using AiProxy.Core.Content.Ccr;

namespace AiProxy.Tests.Unit.Content.Ccr;

/// <summary>An original store in memory that can be told to fail.</summary>
internal sealed class MemoryOriginalStore : IOriginalStore
{
    public ConcurrentDictionary<string, string> Originals { get; } = new();

    public ConcurrentDictionary<string, string> Threads { get; } = new();

    public Exception? Failure { get; set; }

    public ValueTask KeepAsync(KeptOriginal original, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Originals.TryAdd(original.Id, original.Text);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> FindAsync(string id, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return ValueTask.FromResult(Originals.GetValueOrDefault(id));
    }

    public ValueTask<string?> FindThreadAsync(string messageId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return ValueTask.FromResult(Threads.GetValueOrDefault(messageId));
    }

    public ValueTask LinkThreadAsync(string messageId, string continueFrom, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        Threads[messageId] = continueFrom;
        return ValueTask.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (Failure is not null) throw Failure;
    }
}