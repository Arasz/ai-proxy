using System.Text;
using AiProxy.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;

namespace AiProxy.Tests.Unit.Mcp;

public sealed class DrainOnEofStreamTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private const string Request = """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{}}""";

    [Fact]
    public async Task End_of_input_with_no_request_pending_returns_immediately()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), """{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n");

        await DrainAsync(stream).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task End_of_input_waits_until_the_pending_request_is_answered()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), Request + "\n");

        var drain = DrainAsync(stream);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        pending.Complete(new RequestId(7));
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_string_id_is_released_by_the_matching_string_id()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), """{"jsonrpc":"2.0","id":"a-1","method":"ping"}""" + "\n");

        var drain = DrainAsync(stream);
        pending.Complete(new RequestId(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        pending.Complete(new RequestId("a-1"));
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_cancelled_request_no_longer_holds_end_of_input()
    {
        var pending = new PendingRequests();
        var cancel = """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""";
        using var stream = Wrap(pending, new FakeTimeProvider(), Request + "\n" + cancel + "\n");

        await DrainAsync(stream).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task A_line_that_does_not_parse_adds_nothing()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), "not json\n{\"id\":3}\n[1,2]\n");

        await DrainAsync(stream).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task A_last_request_without_a_trailing_newline_is_still_tracked()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), Request);

        var drain = DrainAsync(stream);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        pending.Complete(new RequestId(7));
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task End_of_input_is_released_at_the_bound_when_a_request_never_answers()
    {
        var clock = new TimerObservingTimeProvider();
        var pending = new PendingRequests();
        using var stream = Wrap(pending, clock, Request + "\n");

        var drain = DrainAsync(stream);
        await clock.FirstTimerCreated.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        clock.Advance(Bound);
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, pending.Count);
    }

    [Fact]
    public async Task A_string_id_with_multibyte_characters_survives_one_byte_reads()
    {
        var pending = new PendingRequests();
        var line = """{"jsonrpc":"2.0","id":"é🙂-1","method":"ping"}""" + "\n";
        using var stream = new DrainOnEofStream(new OneByteStream(Encoding.UTF8.GetBytes(line)), pending, new FakeTimeProvider(), Bound, NullLogger.Instance);

        var drain = DrainAsync(stream);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        pending.Complete(new RequestId("é🙂-1"));
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Two_request_lines_in_one_read_are_both_tracked()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), """{"id":1,"method":"a"}""" + "\n" + """{"id":2,"method":"b"}""" + "\n");

        var drain = DrainAsync(stream);
        pending.Complete(new RequestId(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        pending.Complete(new RequestId(2));
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_line_ending_in_carriage_return_and_newline_is_tracked()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), Request + "\r\n");

        var drain = DrainAsync(stream);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        pending.Complete(new RequestId(7));
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_oversized_line_is_ignored_and_the_next_line_is_tracked()
    {
        var pending = new PendingRequests();
        var huge = """{"id":1,"method":"a","params":"%s"}""".Replace("%s", new string('x', DrainOnEofStream.MaxLineBytes + 10));
        using var stream = Wrap(pending, new FakeTimeProvider(), huge + "\n" + Request + "\n");

        var drain = DrainAsync(stream);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal([new RequestId(7)], pending.Ids);

        pending.Complete(new RequestId(7));
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_read_on_a_stream_that_never_answers_is_cancellable()
    {
        using var stream = new DrainOnEofStream(new NeverStream(), new PendingRequests(), new FakeTimeProvider(), Bound, NullLogger.Instance);
        using var cancel = new CancellationTokenSource();
        var read = stream.ReadAsync(new byte[8].AsMemory(), cancel.Token).AsTask();
        var readArray = stream.ReadAsync(new byte[8], 0, 8, cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readArray.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_synchronous_read_passes_bytes_through_and_tracks_requests()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), Request + "\n");

        var buffer = new byte[256];
        var read = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(Request.Length + 1, read);
        Assert.Equal([new RequestId(7)], pending.Ids);
    }

    [Fact]
    public async Task Giving_up_at_the_bound_logs_the_unanswered_ids()
    {
        var clock = new TimerObservingTimeProvider();
        var logger = new CapturingLogger();
        using var stream = Wrap(new PendingRequests(), clock, Request + "\n", logger);

        var drain = DrainAsync(stream);
        await clock.FirstTimerCreated.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Advance(Bound);
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Contains("ids: 7", entry);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception));
    }

    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class NeverStream : Stream
    {
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

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(new TaskCompletionSource<int>().Task);
    }

    private static DrainOnEofStream Wrap(PendingRequests pending, TimeProvider clock, string input, ILogger? logger = null) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(input)), pending, clock, Bound, logger ?? NullLogger.Instance);

    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[256];
        while (await stream.ReadAsync(buffer, TestContext.Current.CancellationToken) > 0)
        {
        }
    }
}
