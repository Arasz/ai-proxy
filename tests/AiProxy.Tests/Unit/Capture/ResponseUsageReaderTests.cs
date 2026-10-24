using System.IO.Compression;
using System.Text;
using AiProxy.Core.Capture;

namespace AiProxy.Tests.Unit.Capture;

/// <summary>Reading token usage out of a provider response body, as it arrived on the wire.</summary>
public sealed class ResponseUsageReaderTests
{
    private const string Json = "application/json";
    private const string EventStream = "text/event-stream; charset=utf-8";

    // Written out, not read from the reader, so a changed limit fails here.
    private const int SixteenMiB = 16 * 1024 * 1024;

    private const string MessageBody = """
        {"id":"msg_1","type":"message","content":[{"type":"text","text":"hi"}],
         "usage":{"input_tokens":11,"output_tokens":22,"cache_creation_input_tokens":33,"cache_read_input_tokens":44,
                  "cache_creation":{"ephemeral_5m_input_tokens":33},"server_tool_use":{"web_search_requests":1},"service_tier":"standard"}}
        """;

    private const string Stream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":100,"output_tokens":1,"cache_creation_input_tokens":200,"cache_read_input_tokens":300,"cache_creation":{"ephemeral_1h_input_tokens":0}}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"usage"}}

        event: ping
        data: {"type": "ping"}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":57,"cache_read_input_tokens":301,"server_tool_use":{"web_search_requests":0}}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    [Fact]
    public void A_message_body_gives_its_four_token_counts_and_ignores_the_rest()
    {
        var reading = ResponseUsageReader.Read(Encoding.UTF8.GetBytes(MessageBody), Json, null);

        Assert.Null(reading.Error);
        Assert.Equal(new ResponseUsage(11, 22, 33, 44), reading.Usage);
    }

    [Fact]
    public void A_stream_takes_the_start_counts_and_lets_the_final_delta_win()
    {
        var reading = ResponseUsageReader.Read(Encoding.UTF8.GetBytes(Stream), EventStream, null);

        Assert.Null(reading.Error);
        Assert.Equal(new ResponseUsage(InputTokens: 100, OutputTokens: 57, CacheCreationInputTokens: 200, CacheReadInputTokens: 301), reading.Usage);
    }

    [Fact]
    public void A_stream_with_crlf_line_endings_reads_the_same()
    {
        var crlf = Stream.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

        var reading = ResponseUsageReader.Read(Encoding.UTF8.GetBytes(crlf), EventStream, null);

        Assert.Equal(new ResponseUsage(100, 57, 200, 301), reading.Usage);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void A_non_string_event_type_is_malformed_and_keeps_preceding_usage(string type)
    {
        var invalidEvent = $"data: {{\"type\":{type}}}\n\n";
        var alone = ResponseUsageReader.Read(Encoding.UTF8.GetBytes(invalidEvent), EventStream, null);
        var afterUsage = ResponseUsageReader.Read(Encoding.UTF8.GetBytes(Stream + invalidEvent), EventStream, null);

        Assert.Null(alone.Usage);
        Assert.Equal("malformed", alone.Error);
        Assert.Equal(new ResponseUsage(100, 57, 200, 301), afterUsage.Usage);
        Assert.Equal("truncated", afterUsage.Error);
    }

    [Fact]
    public void A_delta_null_does_not_erase_a_count_the_start_gave()
    {
        const string stream = """
            data: {"type":"message_start","message":{"usage":{"input_tokens":5,"output_tokens":1}}}

            data: {"type":"message_delta","usage":{"input_tokens":null,"output_tokens":9}}

            """;

        var reading = ResponseUsageReader.Read(Encoding.UTF8.GetBytes(stream), EventStream, null);

        Assert.Equal(new ResponseUsage(5, 9, null, null), reading.Usage);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    [InlineData("GZIP")]
    public void An_encoded_body_is_decoded_before_it_is_read(string encoding)
    {
        var encoded = Encode(Encoding.UTF8.GetBytes(MessageBody), encoding.ToLowerInvariant());

        var reading = ResponseUsageReader.Read(encoded, Json, encoding);

        Assert.Null(reading.Error);
        Assert.Equal(new ResponseUsage(11, 22, 33, 44), reading.Usage);
    }

    [Fact]
    public void An_encoding_the_reader_does_not_know_is_named_in_the_error()
    {
        var reading = ResponseUsageReader.Read(Encoding.UTF8.GetBytes(MessageBody), Json, "zstd");

        Assert.Null(reading.Usage);
        Assert.Equal("encoding:zstd", reading.Error);
    }

    [Theory]
    [InlineData("gzip", "ÿþ not an encoded body at all")]
    [InlineData("deflate", "ÿþ not an encoded body at all")]
    [InlineData("br", "\u0000\u0001garbage")]
    [InlineData("gzip", "\u0000\u0001garbage")]
    public void A_body_that_does_not_decode_is_an_error_not_an_exception(string encoding, string body)
    {
        var reading = ResponseUsageReader.Read(Encoding.Latin1.GetBytes(body), Json, encoding);

        Assert.Null(reading.Usage);
        Assert.Equal(ResponseUsageReader.DecodeFailed, reading.Error);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public void A_compressed_body_cut_short_is_a_decode_failure(string encoding)
    {
        var encoded = Encode(Encoding.UTF8.GetBytes(LongMessageBody()), encoding);

        foreach (var cut in new[] { encoded[..(encoded.Length / 2)], encoded[..^2] })
        {
            var reading = ResponseUsageReader.Read(cut, Json, encoding);

            Assert.Null(reading.Usage);
            Assert.Equal(ResponseUsageReader.DecodeFailed, reading.Error);
        }
    }

    [Fact]
    public void A_raw_deflate_body_sent_as_deflate_is_read()
    {
        var encoded = Encode(Encoding.UTF8.GetBytes(MessageBody), "raw-deflate");

        var reading = ResponseUsageReader.Read(encoded, Json, "deflate");

        Assert.Null(reading.Error);
        Assert.Equal(new ResponseUsage(11, 22, 33, 44), reading.Usage);
    }

    [Fact]
    public void A_body_that_decodes_past_the_limit_is_over_the_cap()
    {
        var huge = new byte[SixteenMiB + 1];
        Array.Fill(huge, (byte)' ');

        var reading = ResponseUsageReader.Read(Encode(huge, "gzip"), Json, "gzip");

        Assert.Null(reading.Usage);
        Assert.Equal(ResponseUsageReader.OverCap, reading.Error);
    }

    [Fact]
    public void A_body_that_decodes_to_exactly_the_limit_is_read()
    {
        var body = Encoding.UTF8.GetBytes("""{"usage":{"input_tokens":7}}""");
        var padded = new byte[SixteenMiB];
        Array.Fill(padded, (byte)' ');
        body.CopyTo(padded, 0);

        var reading = ResponseUsageReader.Read(Encode(padded, "gzip"), Json, "gzip");

        Assert.Equal(new ResponseUsage(7, null, null, null), reading.Usage);
    }

    [Theory]
    [InlineData("""{"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""")]
    [InlineData("""{"usage":{}}""")]
    public void A_usage_object_without_anthropic_counts_is_no_usage(string body)
    {
        var reading = ResponseUsageReader.Read(Encoding.UTF8.GetBytes(body), Json, null);

        Assert.Null(reading.Usage);
        Assert.Equal(ResponseUsageReader.NoUsage, reading.Error);
    }

    [Fact]
    public void A_done_marker_ends_a_stream_without_an_error()
    {
        var reading = ResponseUsageReader.Read(Encoding.UTF8.GetBytes($"{Stream}data: [DONE]\n\n"), EventStream, null);

        Assert.Null(reading.Error);
        Assert.Equal(new ResponseUsage(100, 57, 200, 301), reading.Usage);
    }

    [Fact]
    public void A_stream_cut_mid_event_keeps_the_usage_read_so_far_and_says_it_was_truncated()
    {
        const string cut = """
            data: {"type":"message_start","message":{"usage":{"input_tokens":5,"output_tokens":1}}}

            data: {"type":"message_delta","usage":{"output_to
            """;

        var reading = ResponseUsageReader.Read(Encoding.UTF8.GetBytes(cut), EventStream, null);

        Assert.Equal(new ResponseUsage(5, 1, null, null), reading.Usage);
        Assert.Equal(ResponseUsageReader.Truncated, reading.Error);
    }

    private static string LongMessageBody() =>
        $$"""{"usage":{"input_tokens":11,"output_tokens":22},"content":[{{string.Join(",", Enumerable.Range(0, 400).Select(index => $$"""{"type":"text","text":"line {{index}} of a long answer"}"""))}}]}""";

    [Theory]
    [InlineData(Json, "{\"usage\":{\"input_tokens\":1,")]
    [InlineData(Json, "<html>502 Bad Gateway</html>")]
    [InlineData(EventStream, "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":1}\n\n")]
    public void Malformed_input_is_an_error_not_an_exception(string contentType, string body)
    {
        var reading = ResponseUsageReader.Read(Encoding.UTF8.GetBytes(body), contentType, null);

        Assert.Null(reading.Usage);
        Assert.Equal("malformed", reading.Error);
    }

    [Theory]
    [InlineData(Json, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""")]
    [InlineData(Json, "")]
    [InlineData(EventStream, "event: ping\ndata: {\"type\":\"ping\"}\n\n")]
    public void A_body_without_usage_says_so(string contentType, string body)
    {
        var reading = ResponseUsageReader.Read(Encoding.UTF8.GetBytes(body), contentType, null);

        Assert.Null(reading.Usage);
        Assert.Equal("no-usage", reading.Error);
    }

    [Fact]
    public void A_count_that_is_not_a_number_is_left_out()
    {
        var reading = ResponseUsageReader.Read("""{"usage":{"input_tokens":"many","output_tokens":3}}"""u8.ToArray(), Json, null);

        Assert.Equal(new ResponseUsage(null, 3, null, null), reading.Usage);
    }

    private static byte[] Encode(byte[] bytes, string encoding)
    {
        using var output = new MemoryStream();
        using (Stream encoder = encoding switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true),
            "deflate" => new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true),
            "raw-deflate" => new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        })
        {
            encoder.Write(bytes);
        }

        return output.ToArray();
    }
}
