using System.Buffers;
using System.Text;

namespace AiProxy.Core.Capture;

/// <summary>Keeps what of a response body is needed to read its usage, as the bytes pass to the client.</summary>
public sealed class ResponseBuffer
{
    /// <summary>The most bytes kept of a body that is kept whole.</summary>
    private const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>The bytes kept from the start of a plain event stream.</summary>
    public const int HeadBytes = 64 * 1024;

    /// <summary>The bytes kept from the end of a plain event stream.</summary>
    public const int TailBytes = 64 * 1024;

    /// <summary>
    ///     Starts a line holding the kept end of a Responses terminal event that began before the tail; the reader takes
    ///     its usage from it. An SSE comment, so nothing else reads it as an event.
    /// </summary>
    internal const string CutTerminalLine = ":ai-proxy-cut-terminal ";

    /// <summary>The media type of a server-sent event stream.</summary>
    public const string EventStreamMediaType = "text/event-stream";

    /// <summary>The content encoding that means the body is sent as is.</summary>
    public const string IdentityEncoding = "identity";

    private readonly Lock _gate = new();
    private bool _reading;
    private IStrategy? _strategy;

    private ResponseBuffer()
    {
    }

    /// <summary>Every byte that passed, kept or not.</summary>
    public long Length { get; private set; }

    /// <summary>Whether a body kept whole outgrew <see cref="MaxBytes" />, so it was dropped.</summary>
    public bool OverCap { get; private set; }

    /// <summary>
    ///     Runs <paramref name="reader" /> over the kept bytes, empty when <see cref="OverCap" /> or released. The memory is
    ///     lent from a pool for the length of the call: the reader must not keep it, or any slice of it, and must not
    ///     call <see cref="Append" /> or <see cref="Release" />.
    /// </summary>
    public T Read<T>(Func<ReadOnlyMemory<byte>, T> reader)
    {
        lock (_gate)
        {
            if (_strategy is null)
            {
                return reader(ReadOnlyMemory<byte>.Empty);
            }

            _reading = true;
            try
            {
                return _strategy.Read(reader);
            }
            finally
            {
                _reading = false;
            }
        }
    }

    /// <summary>A buffer for a response with no known content type or encoding: kept whole up to the cap.</summary>
    public static ResponseBuffer Create() => new() { _strategy = new WholeBody(ArrayPool<byte>.Shared) };

    /// <summary>
    ///     A plain (not content-encoded) event stream keeps only its head and tail, where Anthropic puts
    ///     <c>message_start</c> and the final <c>message_delta</c>; any other body is kept whole up to the cap.
    ///     An empty <paramref name="contentEncoding" /> means the body is not encoded.
    /// </summary>
    public static ResponseBuffer For(string contentType, string contentEncoding = IdentityEncoding) => For(contentType, contentEncoding, ArrayPool<byte>.Shared);

    /// <summary>As <see cref="For(string, string)" />, renting every array it keeps from <paramref name="pool" />.</summary>
    internal static ResponseBuffer For(string contentType, string contentEncoding, ArrayPool<byte> pool)
    {
        var isUnencoded = string.IsNullOrWhiteSpace(contentEncoding) || contentEncoding.Trim().Equals(IdentityEncoding, StringComparison.OrdinalIgnoreCase);
        var isEventStream = contentType.Contains(EventStreamMediaType, StringComparison.OrdinalIgnoreCase);
        return new ResponseBuffer { _strategy = isUnencoded && isEventStream ? new HeadAndTail(pool) : new WholeBody(pool) };
    }

    /// <summary>Copies passing bytes; never parses, so it costs the stream a copy and no more.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            ThrowIfReading();
            Length += bytes.Length;
            if (_strategy is null || _strategy.Append(bytes))
            {
                return;
            }

            OverCap = true;
            Drop();
        }
    }

    /// <summary>Lets the kept bytes go once usage has been read; a later append keeps nothing. Safe to call more than once.</summary>
    public void Release()
    {
        lock (_gate)
        {
            ThrowIfReading();
            Drop();
        }
    }

    /// <summary>The lock lets the reader's own thread back in, so a change from inside a reader is refused here.</summary>
    private void ThrowIfReading()
    {
        if (_reading)
        {
            throw new InvalidOperationException("A response buffer cannot change while a reader holds its bytes.");
        }
    }

    /// <summary>Gives back what the strategy rented and forgets it, so nothing writes to a returned array.</summary>
    private void Drop()
    {
        _strategy?.Release();
        _strategy = null;
    }

    /// <summary>How a buffer keeps the bytes that pass; dropped once it reports they no longer fit.</summary>
    private interface IStrategy
    {
        /// <summary>Runs the reader over the kept bytes, lent for the length of the call.</summary>
        T Read<T>(Func<ReadOnlyMemory<byte>, T> reader);

        /// <summary>Keeps the bytes; false when they no longer fit.</summary>
        bool Append(ReadOnlySpan<byte> bytes);

        /// <summary>Gives back any rented arrays; called once, after the last append.</summary>
        void Release();
    }

    /// <summary>Keeps every byte up to <see cref="MaxBytes" /> in one rented array; past that it reports the body no longer fits.</summary>
    /// <param name="pool">Lends the array; it grows by renting a larger one, and goes back on <see cref="Release" />.</param>
    private sealed class WholeBody(ArrayPool<byte> pool) : IStrategy
    {
        private const int FirstBytes = 16 * 1024;
        private byte[] _bytes = [];
        private int _capacity;
        private int _length;

        public T Read<T>(Func<ReadOnlyMemory<byte>, T> reader) => reader(_bytes.AsMemory(0, _length));

        public bool Append(ReadOnlySpan<byte> bytes)
        {
            var needed = _length + bytes.Length;
            if (needed > MaxBytes)
            {
                return false;
            }

            if (needed > _capacity)
            {
                Grow(needed);
            }

            bytes.CopyTo(_bytes.AsSpan(_length, bytes.Length));
            _length = needed;
            return true;
        }

        public void Release()
        {
            if (_capacity > 0)
            {
                pool.Return(_bytes);
            }

            _bytes = [];
            _capacity = 0;
            _length = 0;
        }

        /// <summary>Moves the kept bytes to a rented array of at least twice the size, and gives the old one back.</summary>
        private void Grow(int needed)
        {
            var capacity = Math.Min(MaxBytes, Math.Max(needed, Math.Max(FirstBytes, _capacity * 2)));
            var grown = pool.Rent(capacity);
            _bytes.AsSpan(0, _length).CopyTo(grown.AsSpan(0, capacity));
            if (_capacity > 0)
            {
                pool.Return(_bytes);
            }

            _bytes = grown;
            _capacity = capacity;
        }
    }

    /// <summary>
    ///     The first <see cref="HeadBytes" /> and a rolling last <see cref="TailBytes" />. It also notes where the last
    ///     Responses terminal event line starts and ends, so a terminal event longer than the tail keeps its end.
    /// </summary>
    /// <param name="pool">Lends the head and tail; they go back on <see cref="Release" />.</param>
    private sealed class HeadAndTail(ArrayPool<byte> pool) : IStrategy
    {
        private const int LineHeadBytes = 64;
        private const byte NewLineByte = (byte)'\n';
        private const string ResponseCompleted = "response.completed";
        private const string ResponseIncomplete = "response.incomplete";
        private const string ResponseFailed = "response.failed";
        private const string DataPrefix = "data:";
        private const string SpacedDataPrefix = "data: ";

        /// <summary>The line starts of a Responses terminal event, with and without the optional space after <c>data:</c>.</summary>
        private static readonly byte[][] TerminalStarts =
        [
            .. new[] { ResponseCompleted, ResponseIncomplete, ResponseFailed }
                .SelectMany(static type => new[] { SpacedDataPrefix, DataPrefix }.Select(prefix => $"{prefix}{{\"type\":\"{type}\""))
                .Select(static start => Encoding.UTF8.GetBytes(start))
        ];

        private readonly byte[] _head = pool.Rent(HeadBytes);
        private readonly byte[] _lineHead = new byte[LineHeadBytes];
        private readonly byte[] _tail = pool.Rent(TailBytes);
        private long _afterHead;
        private int _headCount;
        private bool _lineChecked;
        private int _lineHeadCount;
        private long _lineStart;
        private long _position;
        private long _terminalEnd = -1;
        private long _terminalStart = -1;

        /// <summary>The cut-terminal line start, as bytes.</summary>
        private static readonly byte[] CutTerminalBytes = Encoding.ASCII.GetBytes(CutTerminalLine);

        public T Read<T>(Func<ReadOnlyMemory<byte>, T> reader)
        {
            var bound = _headCount + (int)Math.Min(_afterHead, TailBytes) + CutTerminalBytes.Length + 2;
            var stitched = pool.Rent(bound);
            try
            {
                var length = Stitch(stitched.AsSpan(0, bound));
                return reader(stitched.AsMemory(0, length));
            }
            finally
            {
                pool.Return(stitched);
            }
        }

        public void Release()
        {
            pool.Return(_head);
            pool.Return(_tail);
        }

        /// <summary>
        ///     Writes the head and the tail in stream order and returns how many bytes it wrote. When bytes were dropped
        ///     between the two, each side is cut back to a whole line, and a cut Responses terminal event keeps its end
        ///     after <see cref="CutTerminalLine" />.
        /// </summary>
        private int Stitch(Span<byte> destination)
        {
            var head = _head.AsSpan(0, _headCount);
            var tail = _tail.AsSpan(0, TailBytes);
            if (_afterHead <= TailBytes)
            {
                head.CopyTo(destination);
                tail[..(int)_afterHead].CopyTo(destination[_headCount..]);
                return _headCount + (int)_afterHead;
            }

            var start = (int)(_afterHead % TailBytes);
            var headEnd = head.LastIndexOf(NewLineByte) + 1;
            var tailStart = FirstNewLine(tail, start) + 1;
            head[..headEnd].CopyTo(destination);
            var at = headEnd;
            destination[at++] = NewLineByte;
            var kept = _position - TailBytes;
            if (_terminalStart >= 0 && _terminalStart < kept && (_terminalEnd < 0 || _terminalEnd >= kept))
            {
                var end = tailStart == 0 ? TailBytes : tailStart - 1;
                CutTerminalBytes.CopyTo(destination[at..]);
                at += CutTerminalBytes.Length;
                at += CopyInOrder(tail, start, 0, end, destination[at..]);
                destination[at++] = NewLineByte;
                return at + CopyInOrder(tail, start, end, TailBytes - end, destination[at..]);
            }

            return at + CopyInOrder(tail, start, tailStart, TailBytes - tailStart, destination[at..]);
        }

        /// <summary>The stream-order index of the first newline in the rolling tail that starts at <paramref name="start" />; -1 when none.</summary>
        private static int FirstNewLine(ReadOnlySpan<byte> tail, int start)
        {
            var late = tail[start..].IndexOf(NewLineByte);
            if (late >= 0)
            {
                return late;
            }

            var early = tail[..start].IndexOf(NewLineByte);
            return early < 0 ? -1 : tail.Length - start + early;
        }

        /// <summary>Copies <paramref name="count" /> bytes from stream-order index <paramref name="from" /> of the rolling tail; returns the count.</summary>
        private static int CopyInOrder(ReadOnlySpan<byte> tail, int start, int from, int count, Span<byte> destination)
        {
            var physical = (start + from) % tail.Length;
            var first = Math.Min(count, tail.Length - physical);
            tail.Slice(physical, first).CopyTo(destination);
            tail[..(count - first)].CopyTo(destination[first..]);
            return count;
        }

        private static bool IsTerminal(ReadOnlySpan<byte> lineHead)
        {
            foreach (var terminal in TerminalStarts)
            {
                if (lineHead.StartsWith(terminal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Follows line starts and remembers the span of the last Responses terminal event line.</summary>
        private void Track(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                var newline = bytes.IndexOf(NewLineByte);
                var part = newline < 0 ? bytes : bytes[..newline];
                if (!_lineChecked)
                {
                    CheckLine(part, newline);
                }

                if (newline < 0)
                {
                    _position += bytes.Length;
                    return;
                }

                _position += newline + 1;
                if (_terminalStart == _lineStart)
                {
                    _terminalEnd = _position - 1;
                }

                _lineStart = _position;
                _lineHeadCount = 0;
                _lineChecked = false;
                bytes = bytes[(newline + 1)..];
            }
        }

        private void CheckLine(ReadOnlySpan<byte> part, int newline)
        {
            var count = Math.Min(part.Length, LineHeadBytes - _lineHeadCount);
            part[..count].CopyTo(_lineHead.AsSpan(_lineHeadCount));
            _lineHeadCount += count;
            if (_lineHeadCount != LineHeadBytes && newline < 0)
            {
                return;
            }

            _lineChecked = true;
            if (!IsTerminal(_lineHead.AsSpan(0, _lineHeadCount)))
            {
                return;
            }

            _terminalStart = _lineStart;
            _terminalEnd = -1;
        }

        public bool Append(ReadOnlySpan<byte> bytes)
        {
            Track(bytes);
            var toHead = Math.Min(bytes.Length, HeadBytes - _headCount);
            bytes[..toHead].CopyTo(_head.AsSpan(_headCount, HeadBytes - _headCount));
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
                bytes[..count].CopyTo(_tail.AsSpan(position, TailBytes - position));
                _afterHead += count;
                bytes = bytes[count..];
            }

            return true;
        }
    }
}
