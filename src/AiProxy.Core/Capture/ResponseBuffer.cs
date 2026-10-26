namespace AiProxy.Core.Capture;

/// <summary>Keeps what of a response body is needed to read its usage, as the bytes pass to the client.</summary>
public sealed class ResponseBuffer
{
    /// <summary>The most bytes kept of a body that is kept whole.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>The bytes kept from the start of a plain event stream.</summary>
    public const int HeadBytes = 64 * 1024;

    /// <summary>The bytes kept from the end of a plain event stream.</summary>
    public const int TailBytes = 64 * 1024;

    private IStrategy? _strategy;

    private ResponseBuffer()
    {
    }

    /// <summary>Every byte that passed, kept or not.</summary>
    public long Length { get; private set; }

    /// <summary>Whether a body kept whole outgrew <see cref="MaxBytes" />, so it was dropped.</summary>
    public bool OverCap { get; private set; }

    /// <summary>The bytes to read usage from; empty when <see cref="OverCap" /> or released.</summary>
    public ReadOnlyMemory<byte> Body => _strategy?.Body ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>
    ///     A plain (not content-encoded) event stream keeps only its head and tail, where Anthropic puts
    ///     <c>message_start</c> and the final <c>message_delta</c>; any other body is kept whole up to the cap.
    /// </summary>
    public static ResponseBuffer For(string? contentType, string? contentEncoding)
    {
        var plain = string.IsNullOrWhiteSpace(contentEncoding) || contentEncoding.Trim().Equals("identity", StringComparison.OrdinalIgnoreCase);
        var stream = contentType is not null && contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
        return new ResponseBuffer { _strategy = plain && stream ? new Window() : new Whole() };
    }

    /// <summary>Copies passing bytes; never parses, so it costs the stream a copy and no more.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        Length += bytes.Length;
        if (_strategy is null || _strategy.Append(bytes))
        {
            return;
        }

        OverCap = true;
        _strategy = null;
    }

    /// <summary>Lets the kept bytes go once usage has been read.</summary>
    public void Release()
    {
        _strategy = null;
    }

    private interface IStrategy
    {
        ReadOnlyMemory<byte> Body { get; }

        /// <summary>Keeps the bytes; false when they no longer fit.</summary>
        bool Append(ReadOnlySpan<byte> bytes);
    }

    private sealed class Whole : IStrategy
    {
        private readonly MemoryStream _bytes = new();

        public ReadOnlyMemory<byte> Body => _bytes.GetBuffer().AsMemory(0, (int)_bytes.Length);

        public bool Append(ReadOnlySpan<byte> bytes)
        {
            if (_bytes.Length + bytes.Length > MaxBytes)
            {
                return false;
            }

            _bytes.Write(bytes);
            return true;
        }
    }

    /// <summary>The first <see cref="HeadBytes" /> and a rolling last <see cref="TailBytes" />.</summary>
    private sealed class Window : IStrategy
    {
        private readonly byte[] _head = new byte[HeadBytes];
        private readonly byte[] _tail = new byte[TailBytes];
        private long _afterHead;
        private int _headCount;

        public ReadOnlyMemory<byte> Body
        {
            get
            {
                var head = _head.AsSpan(0, _headCount);
                if (_afterHead <= TailBytes)
                {
                    var whole = new byte[_headCount + _afterHead];
                    head.CopyTo(whole);
                    _tail.AsSpan(0, (int)_afterHead).CopyTo(whole.AsSpan(_headCount));
                    return whole;
                }

                // Bytes were dropped between the two: cut each side back to a whole line.
                var start = (int)(_afterHead % TailBytes);
                var tail = new byte[TailBytes];
                _tail.AsSpan(start).CopyTo(tail);
                _tail.AsSpan(0, start).CopyTo(tail.AsSpan(TailBytes - start));
                var headEnd = head.LastIndexOf((byte)'\n') + 1;
                var tailStart = tail.AsSpan().IndexOf((byte)'\n') + 1;
                var stitched = new byte[headEnd + 1 + (TailBytes - tailStart)];
                head[..headEnd].CopyTo(stitched);
                stitched[headEnd] = (byte)'\n';
                tail.AsSpan(tailStart).CopyTo(stitched.AsSpan(headEnd + 1));
                return stitched;
            }
        }

        public bool Append(ReadOnlySpan<byte> bytes)
        {
            var toHead = Math.Min(bytes.Length, HeadBytes - _headCount);
            bytes[..toHead].CopyTo(_head.AsSpan(_headCount));
            _headCount += toHead;
            bytes = bytes[toHead..];
            if (bytes.Length > TailBytes)
            {
                _afterHead += bytes.Length - TailBytes;
                bytes = bytes[^TailBytes..];
            }

            while (!bytes.IsEmpty)
            {
                var position = (int)(_afterHead % TailBytes);
                var count = Math.Min(bytes.Length, TailBytes - position);
                bytes[..count].CopyTo(_tail.AsSpan(position));
                _afterHead += count;
                bytes = bytes[count..];
            }

            return true;
        }
    }
}