using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using AiProxy.Core.Capture;

namespace AiProxy.Tests.Unit.Capture;

/// <summary>An event-stream buffer rents its head and tail, gives them back once, and never writes to them after that.</summary>
public sealed class ResponseBufferPoolTests
{
    private const string EventStream = "text/event-stream";

    [Fact]
    public void A_warm_event_stream_buffer_allocates_no_head_or_tail()
    {
        ResponseBuffer.For(EventStream).Release();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var buffer = ResponseBuffer.For(EventStream);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        buffer.Release();

        Assert.True(allocated < 16 * 1024, $"For allocated {allocated} bytes");
    }

    [Fact]
    public void Release_returns_each_rented_array_once_even_when_called_twice()
    {
        var pool = new RecordingPool();
        var buffer = ResponseBuffer.For(EventStream, ResponseBuffer.IdentityEncoding, pool);
        buffer.Append("data: {}\n\n"u8);

        buffer.Release();
        buffer.Release();

        Assert.Equal(2, pool.Rented.Count);
        Assert.Equal(pool.Rented.OrderBy(RuntimeHelpers.GetHashCode), pool.Returned.OrderBy(RuntimeHelpers.GetHashCode));
    }

    [Fact]
    public void Append_after_release_leaves_the_returned_arrays_alone()
    {
        var pool = new RecordingPool();
        var buffer = ResponseBuffer.For(EventStream, ResponseBuffer.IdentityEncoding, pool);
        buffer.Append("data: {}\n\n"u8);
        buffer.Release();
        var snapshots = pool.Returned.Select(static array => array.ToArray()).ToList();

        buffer.Append(Encoding.UTF8.GetBytes(new string('x', 200_000)));

        Assert.All(pool.Returned.Zip(snapshots), pair => Assert.Equal(pair.Second, pair.First));
        Assert.True(buffer.Body.IsEmpty);
    }

    [Fact]
    public void The_body_read_before_release_is_not_a_pooled_array()
    {
        var pool = new RecordingPool();
        var buffer = ResponseBuffer.For(EventStream, ResponseBuffer.IdentityEncoding, pool);
        buffer.Append("data: {\"type\":\"message_start\"}\n\n"u8);

        var body = buffer.Body;
        buffer.Release();

        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(body, out var segment));
        Assert.DoesNotContain(segment.Array!, pool.Rented);
        Assert.Equal("data: {\"type\":\"message_start\"}\n\n", Encoding.UTF8.GetString(body.Span));
    }

    [Fact]
    public void A_pool_that_lends_longer_arrays_keeps_the_same_body()
    {
        var stream = new StringBuilder();
        for (var index = 0; index < 20_000; index++)
        {
            stream.Append("data: {\"type\":\"delta\",\"n\":").Append(index).Append("}\n\n");
        }

        var bytes = Encoding.UTF8.GetBytes(stream.ToString());
        Assert.True(bytes.Length > ResponseBuffer.HeadBytes + 2 * ResponseBuffer.TailBytes, "the tail must wrap");
        var exact = ResponseBuffer.For(EventStream, ResponseBuffer.IdentityEncoding, new RecordingPool());
        var longer = ResponseBuffer.For(EventStream, ResponseBuffer.IdentityEncoding, new RecordingPool(extra: ResponseBuffer.TailBytes));

        exact.Append(bytes);
        longer.Append(bytes);

        Assert.Equal(exact.Body.ToArray(), longer.Body.ToArray());
    }

    /// <summary>Hands out new arrays and records what was rented and returned.</summary>
    /// <param name="extra">How many bytes longer than asked each array is.</param>
    private sealed class RecordingPool(int extra = 0) : ArrayPool<byte>
    {
        public List<byte[]> Rented { get; } = [];

        public List<byte[]> Returned { get; } = [];

        public override byte[] Rent(int minimumLength)
        {
            var array = new byte[minimumLength + extra];
            Rented.Add(array);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false) => Returned.Add(array);
    }
}
