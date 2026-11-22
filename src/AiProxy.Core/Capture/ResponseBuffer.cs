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
    private IStrategy? _strategy;

    private ResponseBuffer()
    {
    }

    /// <summary>Every byte that passed, kept or not.</summary>
    public long Length { get; private set; }

    /// <summary>Whether a body kept whole outgrew <see cref="MaxBytes" />, so it was dropped.</summary>
    public bool OverCap { get; private set; }

    /// <summary>The bytes to read usage from; empty when <see cref="OverCap" /> or released.</summary>
    public ReadOnlyMemory<byte> Body
    {
        get
        {
            lock (_gate)
            {
                return _strategy?.Body ?? ReadOnlyMemory<byte>.Empty;
            }
        }
    }

    /// <summary>A buffer for a response with no known content type or encoding: kept whole up to the cap.</summary>
    public static ResponseBuffer Create() => new() { _strategy = new WholeBody() };

    /// <summary>
    ///     A plain (not content-encoded) event stream keeps only its head and tail, where Anthropic puts
    ///     <c>message_start</c> and the final <c>message_delta</c>; any other body is kept whole up to the cap.
    ///     An empty <paramref name="contentEncoding" /> means the body is not encoded.
    /// </summary>
    public static ResponseBuffer For(string contentType, string contentEncoding = IdentityEncoding) => For(contentType, contentEncoding, ArrayPool<byte>.Shared);

    /// <summary>As <see cref="For(string, string)" />, renting an event stream's head and tail from <paramref name="pool" />.</summary>
    public static ResponseBuffer For(string contentType, string contentEncoding, ArrayPool<byte> pool)
    {
        var isUnencoded = string.IsNullOrWhiteSpace(contentEncoding) || contentEncoding.Trim().Equals(IdentityEncoding, StringComparison.OrdinalIgnoreCase);
        var isEventStream = contentType.Contains(EventStreamMediaType, StringComparison.OrdinalIgnoreCase);
        return new ResponseBuffer { _strategy = isUnencoded && isEventStream ? new HeadAndTail(pool) : new WholeBody() };
    }

    /// <summary>Copies passing bytes; never parses, so it costs the stream a copy and no more.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
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
            Drop();
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
        /// <summary>The kept bytes, as the usage reader sees them.</summary>
        ReadOnlyMemory<byte> Body { get; }

        /// <summary>Keeps the bytes; false when they no longer fit.</summary>
        bool Append(ReadOnlySpan<byte> bytes);

        /// <summary>Gives back any rented arrays; called once, after the last append.</summary>
        void Release();
    }

    /// <summary>Keeps every byte up to <see cref="MaxBytes" />; past that it reports the body no longer fits.</summary>
    private sealed class WholeBody : IStrategy
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

        public void Release()
        {
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
                var headEnd = head.LastIndexOf(NewLineByte) + 1;
                var tailStart = tail.AsSpan().IndexOf(NewLineByte) + 1;
                var kept = _position - TailBytes;
                if (_terminalStart >= 0 && _terminalStart < kept && (_terminalEnd < 0 || _terminalEnd >= kept))
                {
                    return StitchCutTerminal(head[..headEnd], tail, tailStart);
                }

                var stitched = new byte[headEnd + 1 + (TailBytes - tailStart)];
                head[..headEnd].CopyTo(stitched);
                stitched[headEnd] = NewLineByte;
                tail.AsSpan(tailStart).CopyTo(stitched.AsSpan(headEnd + 1));
                return stitched;
            }
        }

        public void Release()
        {
            pool.Return(_head);
            pool.Return(_tail);
        }

        private static byte[] StitchCutTerminal(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, int tailStart)
        {
            var end = tailStart == 0 ? tail.Length : tailStart - 1;
            var marker = Encoding.ASCII.GetBytes(CutTerminalLine);
            var rest = tail[end..];
            var stitched = new byte[head.Length + 1 + marker.Length + end + rest.Length + 1];
            var at = 0;
            head.CopyTo(stitched);
            at += head.Length;
            stitched[at++] = NewLineByte;
            marker.CopyTo(stitched.AsSpan(at));
            at += marker.Length;
            tail[..end].CopyTo(stitched.AsSpan(at));
            at += end;
            stitched[at++] = NewLineByte;
            rest.CopyTo(stitched.AsSpan(at));
            return stitched;
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
