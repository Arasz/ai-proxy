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
        var original = Encoding.UTF8.GetBytes("""{"model":"gpt-5","input":"hello"}""");

        Assert.Equal(original, ZstdRequestBody.Decode(Encode(original)));
    }

    [Fact]
    public void A_body_cut_short_does_not_decode()
    {
        var encoded = Encode(Encoding.UTF8.GetBytes(new string('x', 10_000) + "tail"));

        Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode(encoded[..(encoded.Length / 2)]));
    }

    [Fact]
    public void Bytes_that_are_not_zstd_do_not_decode()
    {
        Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode([0x28, 0xB5, 0x2F, 0xFD, 0xFF, 0x00, 0x13, 0x37]));
    }

    [Fact]
    public void A_body_that_decodes_past_the_limit_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode(Encode(new byte[SixteenMiB + 1])));
    }

    [Fact]
    public void A_body_that_decodes_to_exactly_the_limit_is_decoded()
    {
        Assert.Equal(SixteenMiB, ZstdRequestBody.Decode(Encode(new byte[SixteenMiB])).Length);
    }

    [Fact]
    public void A_frame_declaring_a_window_above_the_decoder_cap_is_refused_even_when_its_content_is_small()
    {
        var encoded = Encode(Encoding.UTF8.GetBytes("small"), windowLog: 27);

        Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode(encoded));
    }

    [Fact]
    public void A_frame_declaring_a_window_at_the_decoder_cap_is_decoded()
    {
        var original = Encoding.UTF8.GetBytes("small");

        Assert.Equal(original, ZstdRequestBody.Decode(Encode(original, windowLog: 24)));
    }

    [Theory]
    [InlineData("zstd", true)]
    [InlineData(" ZSTD ", true)]
    [InlineData("gzip", false)]
    [InlineData("zstd, gzip", false)]
    [InlineData(null, false)]
    public void Only_a_lone_zstd_encoding_is_decoded(string? encoding, bool expected)
    {
        Assert.Equal(expected, ZstdRequestBody.IsZstd(encoding));
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
