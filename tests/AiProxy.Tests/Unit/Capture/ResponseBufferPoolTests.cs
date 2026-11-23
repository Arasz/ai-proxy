using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using AiProxy.Core.Capture;

namespace AiProxy.Tests.Unit.Capture;

/// <summary>An event-stream buffer rents its head and tail, gives them back once, and never writes to them after that.</summary>
public sealed class ResponseBufferPoolTests
{
    private const string EventStream = "text/event-stream";
    private const string Json = "application/json";

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
        Assert.True(buffer.Read(static kept => kept.IsEmpty));
    }

    [Theory]
    [InlineData(EventStream, ResponseBuffer.IdentityEncoding)]
    [InlineData(Json, ResponseBuffer.IdentityEncoding)]
    public void Read_lends_a_pooled_window_of_exactly_the_kept_length_that_goes_back_once(string contentType, string encoding)
    {
        var pool = new RecordingPool(extra: 1000);
        var buffer = ResponseBuffer.For(contentType, encoding, pool);
        var sent = "data: {\"type\":\"message_start\"}\n\n"u8.ToArray();
        buffer.Append(sent);
        byte[]? lent = null;

        var text = buffer.Read(kept =>
        {
            Assert.True(MemoryMarshal.TryGetArray(kept, out var segment));
            lent = segment.Array;
            Assert.Equal(sent.Length, kept.Length);
            return Encoding.UTF8.GetString(kept.Span);
        });

        buffer.Release();

        Assert.Equal(Encoding.UTF8.GetString(sent), text);
        Assert.Contains(lent!, pool.Rented);
        Assert.Single(pool.Returned, returned => ReferenceEquals(returned, lent));
    }

    [Fact]
    public void A_reader_that_throws_still_gives_the_window_back()
    {
        var pool = new RecordingPool();
        var buffer = ResponseBuffer.For(EventStream, ResponseBuffer.IdentityEncoding, pool);
        buffer.Append("data: {}\n\n"u8);
        var held = pool.Rented.Count;

        Assert.Throws<InvalidOperationException>(() => buffer.Read<int>(static _ => throw new InvalidOperationException()));

        Assert.Equal(pool.Rented.Skip(held), pool.Returned);
    }

    [Theory]
    [InlineData(EventStream)]
    [InlineData(Json)]
    public void Reading_a_warm_buffer_allocates_only_the_reader_result(string contentType)
    {
        var bytes = Stream(20_000);
        Measure(contentType, bytes);

        var allocated = Measure(contentType, bytes);

        Assert.True(allocated < 16 * 1024, $"a {contentType} buffer allocated {allocated} bytes for a {bytes.Length}-byte body");
    }

    [Fact]
    public void A_whole_body_returns_what_it_rented_on_release_and_ignores_later_appends()
    {
        var pool = new RecordingPool();
        var buffer = ResponseBuffer.For(Json, ResponseBuffer.IdentityEncoding, pool);
        buffer.Append(Stream(5_000));
        buffer.Release();
        var snapshots = pool.Returned.Select(static array => array.ToArray()).ToList();

        buffer.Append(Stream(5_000));
        buffer.Release();

        Assert.NotEmpty(pool.Rented);
        Assert.Equal(pool.Rented.OrderBy(RuntimeHelpers.GetHashCode), pool.Returned.OrderBy(RuntimeHelpers.GetHashCode));
        Assert.All(pool.Returned.Zip(snapshots), pair => Assert.Equal(pair.Second, pair.First));
    }

    [Fact]
    public void A_whole_body_over_the_cap_gives_its_arrays_back()
    {
        var pool = new RecordingPool();
        var buffer = ResponseBuffer.For(Json, ResponseBuffer.IdentityEncoding, pool);

        buffer.Append(new byte[3 * 1024 * 1024]);
        buffer.Append(new byte[2 * 1024 * 1024]);

        Assert.True(buffer.OverCap);
        Assert.Equal(pool.Rented.OrderBy(RuntimeHelpers.GetHashCode), pool.Returned.OrderBy(RuntimeHelpers.GetHashCode));
    }

    [Fact]
    public void A_whole_body_from_a_pool_that_lends_longer_arrays_keeps_the_same_bytes()
    {
        var bytes = Stream(5_000);
        var exact = ResponseBuffer.For(Json, ResponseBuffer.IdentityEncoding, new RecordingPool());
        var longer = ResponseBuffer.For(Json, ResponseBuffer.IdentityEncoding, new RecordingPool(extra: 4093));

        for (var offset = 0; offset < bytes.Length; offset += 7919)
        {
            var chunk = bytes.AsSpan(offset, Math.Min(7919, bytes.Length - offset));
            exact.Append(chunk);
            longer.Append(chunk);
        }

        Assert.Equal(bytes, exact.Read(static kept => kept.ToArray()));
        Assert.Equal(bytes, longer.Read(static kept => kept.ToArray()));
    }

    private static long Measure(string contentType, byte[] bytes)
    {
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var buffer = ResponseBuffer.For(contentType);
        for (var offset = 0; offset < bytes.Length; offset += 4096)
        {
            buffer.Append(bytes.AsSpan(offset, Math.Min(4096, bytes.Length - offset)));
        }

        var length = buffer.Read(static kept => kept.Length);
        buffer.Release();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        Assert.True(length > 0);
        return allocated;
    }

    private static byte[] Stream(int events)
    {
        var text = new StringBuilder();
        for (var index = 0; index < events; index++)
        {
            text.Append("data: {\"type\":\"delta\",\"n\":").Append(index).Append("}\n\n");
        }

        return Encoding.UTF8.GetBytes(text.ToString());
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

        Assert.Equal(exact.Read(static kept => kept.ToArray()), longer.Read(static kept => kept.ToArray()));
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
