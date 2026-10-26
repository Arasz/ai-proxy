using System.Text;
using AiProxy.Core.Capture;

namespace AiProxy.Tests.Unit.Capture;

/// <summary>What the capture keeps of a response body as it passes: all of it up to a cap, or a stream's head and tail.</summary>
public sealed class ResponseBufferTests
{
    private const string EventStream = "text/event-stream";

    // Written out, not read from ResponseBuffer, so a changed cap fails here.
    private const int FourMiB = 4 * 1024 * 1024;

    [Fact]
    public void A_body_of_exactly_the_cap_is_kept_whole()
    {
        var buffer = ResponseBuffer.For("application/json", "gzip");
        var body = new byte[FourMiB];
        Random.Shared.NextBytes(body);

        Append(buffer, body, 7919);

        Assert.False(buffer.OverCap);
        Assert.Equal(body.Length, buffer.Length);
        Assert.True(buffer.Body.Span.SequenceEqual(body));
    }

    [Fact]
    public void One_byte_over_the_cap_drops_the_body_and_keeps_counting()
    {
        var buffer = ResponseBuffer.For("application/json", null);

        Append(buffer, new byte[FourMiB + 1], 65536);
        buffer.Append(new byte[10]);

        Assert.True(buffer.OverCap);
        Assert.Equal(FourMiB + 11, buffer.Length);
        Assert.True(buffer.Body.IsEmpty);
    }

    [Fact]
    public void An_encoded_event_stream_is_kept_whole_under_the_cap()
    {
        var buffer = ResponseBuffer.For(EventStream, "gzip");

        Append(buffer, new byte[FourMiB + 1], 65536);

        Assert.True(buffer.OverCap);
    }

    [Fact]
    public void A_short_plain_event_stream_is_kept_byte_for_byte()
    {
        var buffer = ResponseBuffer.For("text/event-stream; charset=utf-8", "identity");
        var stream = Encoding.UTF8.GetBytes(Stream(50));
        Assert.True(stream.Length < ResponseBuffer.HeadBytes + ResponseBuffer.TailBytes, "the stream must fit, or this proves nothing");

        Append(buffer, stream, 333);

        Assert.True(buffer.Body.Span.SequenceEqual(stream));
    }

    [Fact]
    public void A_plain_event_stream_far_over_the_cap_keeps_its_head_and_tail_and_still_reads_whole()
    {
        var buffer = ResponseBuffer.For(EventStream, null);
        var stream = Encoding.UTF8.GetBytes(Stream(60_000));
        Assert.True(stream.Length > FourMiB + 1024, $"the stream is {stream.Length} bytes; it must be over the cap");

        // An odd chunk size cuts events in the middle at both ends of the window.
        Append(buffer, stream, 4093);

        Assert.False(buffer.OverCap);
        Assert.Equal(stream.Length, buffer.Length);
        Assert.True(buffer.Body.Length <= ResponseBuffer.HeadBytes + ResponseBuffer.TailBytes + 1);
        var reading = ResponseUsageReader.Read(buffer.Body, EventStream, null);
        Assert.Null(reading.Error);
        Assert.Equal(new ResponseUsage(100, 57, 200, 301), reading.Usage);
    }

    [Fact]
    public void A_tail_that_starts_inside_a_line_drops_that_part_even_when_it_reads_like_an_event()
    {
        // Answer text that looks like an event line, with the tail's first byte placed on one of those copies:
        // kept, the partial line would read as a broken event and lose the stream its final usage.
        var lead = Stream(30_000).Split("event: message_delta")[0];
        var filler = $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{string.Concat(Enumerable.Repeat("data: {\\\"type\\\":\\\"x\\\"} ", 40))}\"}}}}\n\n";
        const string end = "event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":57,\"cache_read_input_tokens\":301}}\n\n";
        var fillers = ResponseBuffer.TailBytes / filler.Length + 2;
        var text = $"{lead}{string.Concat(Enumerable.Repeat(filler, fillers))}{end}";
        var cut = Encoding.UTF8.GetByteCount(text) - ResponseBuffer.TailBytes;
        var onCopy = text.IndexOf("data: {\\\"type", cut, StringComparison.Ordinal);
        // Padding after that copy moves the cut forward onto it.
        text = text.Insert(text.LastIndexOf("\"}}", StringComparison.Ordinal), new string(' ', onCopy - cut));
        var stream = Encoding.UTF8.GetBytes(text);
        const string copy = "data: {\\\"type";
        Assert.Equal(copy, Encoding.UTF8.GetString(stream, stream.Length - ResponseBuffer.TailBytes, copy.Length));
        var buffer = ResponseBuffer.For(EventStream, null);

        Append(buffer, stream, 4096);

        var reading = ResponseUsageReader.Read(buffer.Body, EventStream, null);
        Assert.Null(reading.Error);
        Assert.Equal(new ResponseUsage(100, 57, 200, 301), reading.Usage);
    }

    [Fact]
    public void A_released_buffer_holds_nothing()
    {
        var buffer = ResponseBuffer.For("application/json", null);
        buffer.Append("{}"u8);

        buffer.Release();

        Assert.True(buffer.Body.IsEmpty);
    }

    private static void Append(ResponseBuffer buffer, byte[] bytes, int chunk)
    {
        for (var offset = 0; offset < bytes.Length; offset += chunk) buffer.Append(bytes.AsSpan(offset, Math.Min(chunk, bytes.Length - offset)));
    }

    private static string Stream(int events)
    {
        var text = new StringBuilder();
        text.Append("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":100,\"output_tokens\":1,\"cache_creation_input_tokens\":200,\"cache_read_input_tokens\":300}}}\n\n");
        for (var index = 0; index < events; index++) text.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"word ").Append(index).Append("\"}}\n\n");

        text.Append("event: message_delta\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":57,\"cache_read_input_tokens\":301}}\n\n");
        text.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        return text.ToString();
    }
}