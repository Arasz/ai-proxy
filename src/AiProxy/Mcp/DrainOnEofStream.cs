using System.Buffers;
using System.Text;

namespace AiProxy.Mcp;

/// <summary>Wraps stdin so end of input waits for in-flight replies, up to a bound, before the transport sees EOF. Reads stay cancellable even when the inner stream ignores the token.</summary>
internal sealed partial class DrainOnEofStream(Stream inner, PendingRequests pending, TimeProvider time, TimeSpan bound, ILogger logger) : Stream
{
    /// <summary>Longest input line that is parsed for ids; a longer line is skipped.</summary>
    public const int MaxLineBytes = 1024 * 1024;

    private readonly ArrayBufferWriter<byte> _line = new();
    private bool _lineTooLong;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            Scan(buffer.AsSpan(offset, read));
        }
        else
        {
            EndLine();
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var pendingRead = inner.ReadAsync(buffer, cancellationToken);
        var read = pendingRead.IsCompletedSuccessfully
            ? pendingRead.Result
            : await pendingRead.AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            Scan(buffer.Span[..read]);
            return read;
        }

        EndLine();
        await WaitForRepliesAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task WaitForRepliesAsync(CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = Task.Delay(bound, time, wait.Token);
        var finished = await Task.WhenAny(pending.WhenEmpty, timeout).ConfigureAwait(false);
        await wait.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (finished == timeout && pending.Count > 0)
        {
            Log.GaveUp(logger, pending.Count, bound.TotalSeconds, string.Join(", ", pending.Ids));
        }
    }

    private void Scan(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            var piece = newline < 0 ? bytes : bytes[..newline];
            if (_lineTooLong || _line.WrittenCount + piece.Length > MaxLineBytes)
            {
                _lineTooLong = true;
            }
            else
            {
                _line.Write(piece);
            }

            if (newline < 0)
            {
                return;
            }

            EndLine();
            bytes = bytes[(newline + 1)..];
        }
    }

    private void EndLine()
    {
        if (!_lineTooLong && _line.WrittenCount > 0)
        {
            pending.Track(Encoding.UTF8.GetString(_line.WrittenSpan));
        }

        _line.Clear();
        _lineTooLong = false;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1601, Level = LogLevel.Warning, Message = "Input closed; gave up after {Seconds}s with {Count} request(s) unanswered, ids: {Ids}")]
        public static partial void GaveUp(ILogger logger, int count, double seconds, string ids);
    }
}
