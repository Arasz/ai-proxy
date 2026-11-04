using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace AiProxy.Mcp;

/// <summary>The JSON-RPC requests read from stdin that have not been answered yet.</summary>
internal sealed class PendingRequests
{
    private readonly HashSet<RequestId> _ids = [];
    private readonly Lock _gate = new();
    private TaskCompletionSource _empty = Completed();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _ids.Count;
            }
        }
    }

    /// <summary>A task that completes while no request is pending.</summary>
    public Task WhenEmpty
    {
        get
        {
            lock (_gate)
            {
                return _empty.Task;
            }
        }
    }

    /// <summary>Records the request on one input line, or forgets the request a cancel notification names; other lines change nothing.</summary>
    public void Track(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var method))
            {
                return;
            }

            if (root.TryGetProperty("id", out var id) && ToRequestId(id) is { } requestId)
            {
                Add(requestId);
            }
            else if (method.ValueKind == JsonValueKind.String && method.GetString() == "notifications/cancelled"
                && root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("requestId", out var target) && ToRequestId(target) is { } cancelled)
            {
                Complete(cancelled);
            }
        }
        catch (JsonException)
        {
        }
    }

    public void Complete(RequestId id)
    {
        lock (_gate)
        {
            if (_ids.Remove(id) && _ids.Count == 0)
            {
                _empty.TrySetResult();
            }
        }
    }

    private void Add(RequestId id)
    {
        lock (_gate)
        {
            if (_ids.Add(id) && _ids.Count == 1)
            {
                _empty = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private static RequestId? ToRequestId(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => new RequestId(element.GetString()!),
        JsonValueKind.Number when element.TryGetInt64(out var number) => new RequestId(number),
        _ => null
    };

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }
}

/// <summary>Wraps stdin so end of input waits for in-flight replies, up to a bound, before the transport sees EOF.</summary>
internal sealed class DrainOnEofStream(Stream inner, PendingRequests pending, TimeProvider time, TimeSpan bound) : Stream
{
    private readonly List<byte> _line = [];

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            Scan(buffer.Span[..read]);
            return read;
        }

        EndLine();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = Task.Delay(bound, time, wait.Token);
        await Task.WhenAny(pending.WhenEmpty, timeout).ConfigureAwait(false);
        await wait.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Scan(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value == (byte)'\n')
            {
                EndLine();
            }
            else
            {
                _line.Add(value);
            }
        }
    }

    private void EndLine()
    {
        if (_line.Count == 0)
        {
            return;
        }

        pending.Track(System.Text.Encoding.UTF8.GetString([.. _line]));
        _line.Clear();
    }
}
