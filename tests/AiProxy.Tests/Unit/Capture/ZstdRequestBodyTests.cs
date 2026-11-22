using System.IO.Compression;
using System.Text;
using AiProxy.Hosting.Integration;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace AiProxy.Tests.Unit.Capture;

/// <summary>Decoding a zstd request body for capture and measurement.</summary>
public sealed class ZstdRequestBodyTests
{
    // Written out, not read from the decoder, so a changed limit fails here.
    private const int SixteenMiB = 16 * 1024 * 1024;

    [Fact]
    public void A_streamed_zstd_body_decodes_to_the_original_bytes()
    {
        var original = """{"model":"gpt-5","input":"hello"}"""u8.ToArray();

        Assert.Equal(original, ZstdRequestBody.Decode(Encode(original)));
    }

    [Fact]
    public void A_body_cut_short_does_not_decode()
    {
        var encoded = Encode(Encoding.UTF8.GetBytes($"{new string('x', 10_000)}tail"));

        Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode(encoded[..(encoded.Length / 2)]));
    }

    [Fact]
    public void Bytes_that_are_not_zstd_do_not_decode() => Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode([0x28, 0xB5, 0x2F, 0xFD, 0xFF, 0x00, 0x13, 0x37]));

    [Fact]
    public void A_body_that_decodes_past_the_limit_is_refused() => Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode(Encode(new byte[SixteenMiB + 1])));

    [Fact]
    public void A_body_that_decodes_to_exactly_the_limit_is_decoded() => Assert.Equal(SixteenMiB, ZstdRequestBody.Decode(Encode(new byte[SixteenMiB])).Length);

    [Fact]
    public void A_frame_declaring_a_window_above_the_decoder_cap_is_refused_even_when_its_content_is_small()
    {
        var encoded = Encode("small"u8.ToArray(), windowLog: 27);

        Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode(encoded));
    }

    [Fact]
    public void A_frame_declaring_a_window_at_the_decoder_cap_is_decoded()
    {
        var original = "small"u8.ToArray();

        Assert.Equal(original, ZstdRequestBody.Decode(Encode(original, windowLog: 24)));
    }

    [Theory]
    [InlineData("zstd", true)]
    [InlineData(" ZSTD ", true)]
    [InlineData("gzip", false)]
    [InlineData("zstd, gzip", false)]
    [InlineData(null, false)]
    public void Only_a_lone_zstd_encoding_is_decoded(string? encoding, bool expected) => Assert.Equal(expected, ZstdRequestBody.IsZstd(encoding));

    [Fact]
    public void A_frame_that_declares_its_size_costs_the_decoded_size_once()
    {
        var original = Text(512 * 1024);
        var encoded = OneShot(original);

        var (decoded, allocated) = DecodeMeasured(encoded);

        Assert.Equal(original, decoded);
        Assert.True(allocated < original.Length + 32 * 1024, $"Decode allocated {allocated} bytes for {original.Length} decoded bytes");
    }

    [Fact]
    public void A_frame_without_a_declared_size_costs_the_decoded_size_once()
    {
        var original = Text(512 * 1024);
        var encoded = Streamed(original);
        Assert.True(ZstandardDecoder.TryGetMaxDecompressedLength(encoded, out var bound) && bound > original.Length, "the frame must not declare its exact size");

        var (decoded, allocated) = DecodeMeasured(encoded);

        Assert.Equal(original, decoded);
        Assert.True(allocated < original.Length + 32 * 1024, $"Decode allocated {allocated} bytes for {original.Length} decoded bytes");
    }

    [Fact]
    public void Two_frames_decode_to_both_bodies_in_order()
    {
        var first = "first frame,"u8.ToArray();
        var second = "second frame"u8.ToArray();

        Assert.Equal([.. first, .. second], ZstdRequestBody.Decode([.. OneShot(first), .. Streamed(second)]));
    }

    [Fact]
    public void Two_frames_that_together_pass_the_limit_are_refused()
    {
        var half = new byte[SixteenMiB / 2 + 1];

        Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode([.. OneShot(half), .. OneShot(half)]));
    }

    private static (byte[] Decoded, long Allocated) DecodeMeasured(byte[] encoded)
    {
        ZstdRequestBody.Decode(encoded);
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var decoded = ZstdRequestBody.Decode(encoded);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        return (decoded, allocated);
    }

    private static byte[] Text(int length)
    {
        var bytes = new byte[length];
        var random = new Random(7);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)('a' + random.Next(8));
        }

        return bytes;
    }

    /// <summary>One frame that declares its decoded size.</summary>
    private static byte[] OneShot(byte[] bytes)
    {
        var encoded = new byte[ZstandardEncoder.GetMaxCompressedLength(bytes.Length)];
        Assert.True(ZstandardEncoder.TryCompress(bytes, encoded, out var written));
        return encoded[..written];
    }

    /// <summary>One frame written in two flushes, so it does not declare its decoded size.</summary>
    private static byte[] Streamed(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var encoder = new ZstandardStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            var half = bytes.Length / 2;
            encoder.Write(bytes, 0, half);
            encoder.Flush();
            encoder.Write(bytes, half, bytes.Length - half);
        }

        return output.ToArray();
    }

    private static byte[] Encode(byte[] bytes, int? windowLog = null)
    {
        using var output = new MemoryStream();
        using (var encoder = new CompressionStream(output, 3, leaveOpen: true))
        {
            if (windowLog is { } log)
            {
                encoder.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, log);
            }

            encoder.Write(bytes);
        }

        return output.ToArray();
    }
}
