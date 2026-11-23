using System.IO.Compression;
using System.Text;
using AiProxy.Hosting.Integration;

namespace AiProxy.Tests.Unit.Capture;

/// <summary>Decoding a zstd request body for capture and measurement.</summary>
[Collection(SharedArrayPoolCollection.Name)]
public sealed class ZstdRequestBodyTests
{
    // Written out, not read from the decoder, so a changed limit fails here.
    private const int SixteenMiB = 16 * 1024 * 1024;

    [Fact]
    public void A_streamed_zstd_body_decodes_to_the_original_bytes()
    {
        var original = """{"model":"gpt-5","input":"hello"}"""u8.ToArray();

        Assert.Equal(original, ZstdRequestBody.Decode(ZstdFrames.Streamed(original)));
    }

    [Fact]
    public void A_body_cut_short_does_not_decode()
    {
        var encoded = ZstdFrames.Streamed(Encoding.UTF8.GetBytes($"{new string('x', 10_000)}tail"));

        Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode(encoded[..(encoded.Length / 2)]));
    }

    [Fact]
    public void Bytes_that_are_not_zstd_do_not_decode() => Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode([0x28, 0xB5, 0x2F, 0xFD, 0xFF, 0x00, 0x13, 0x37]));

    [Fact]
    public void A_body_that_decodes_past_the_limit_is_refused() => Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode(ZstdFrames.Streamed(new byte[SixteenMiB + 1])));

    [Fact]
    public void A_body_that_decodes_to_exactly_the_limit_is_decoded() => Assert.Equal(SixteenMiB, ZstdRequestBody.Decode(ZstdFrames.Streamed(new byte[SixteenMiB])).Length);

    [Fact]
    public void A_frame_declaring_a_window_above_the_decoder_cap_is_refused_even_when_its_content_is_small()
    {
        var encoded = ZstdFrames.Streamed("small"u8.ToArray(), windowLog2: 27);

        Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode(encoded));
    }

    [Fact]
    public void A_frame_declaring_a_window_at_the_decoder_cap_is_decoded()
    {
        var original = "small"u8.ToArray();

        Assert.Equal(original, ZstdRequestBody.Decode(ZstdFrames.Streamed(original, windowLog2: 24)));
    }

    [Theory]
    [InlineData("zstd", true)]
    [InlineData(" ZSTD ", true)]
    [InlineData("gzip", false)]
    [InlineData("zstd, gzip", false)]
    [InlineData(null, false)]
    public void Only_a_lone_zstd_encoding_is_decoded(string? encoding, bool expected) => Assert.Equal(expected, ZstdRequestBody.IsZstd(encoding));

    [Fact]
    public void An_empty_frame_decodes_to_no_bytes() => Assert.Empty(ZstdRequestBody.Decode(ZstdFrames.OneShot([])));

    [Fact]
    public void An_empty_frame_after_a_frame_that_fills_the_bound_is_decoded() =>
        Assert.Equal("{}"u8.ToArray(), ZstdRequestBody.Decode([.. ZstdFrames.OneShot("{}"u8.ToArray()), .. ZstdFrames.OneShot([])]));

    [Fact]
    public void An_empty_frame_after_a_frame_of_exactly_the_limit_is_decoded() =>
        Assert.Equal(SixteenMiB, ZstdRequestBody.Decode([.. ZstdFrames.OneShot(new byte[SixteenMiB]), .. ZstdFrames.OneShot([])]).Length);

    [Fact]
    public void An_empty_body_decodes_to_no_bytes() => Assert.Empty(ZstdRequestBody.Decode([]));

    [Fact]
    public void A_frame_that_declares_its_size_costs_the_decoded_size_once()
    {
        var original = Text(512 * 1024);
        var encoded = ZstdFrames.OneShot(original);

        var (decoded, allocated) = DecodeMeasured(encoded);

        Assert.Equal(original, decoded);
        Assert.True(allocated < original.Length + 32 * 1024, $"Decode allocated {allocated} bytes for {original.Length} decoded bytes");
    }

    [Fact]
    public void A_frame_without_a_declared_size_costs_the_decoded_size_once()
    {
        var original = Text(512 * 1024);
        var encoded = ZstdFrames.Streamed(original);
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

        Assert.Equal([.. first, .. second], ZstdRequestBody.Decode([.. ZstdFrames.OneShot(first), .. ZstdFrames.Streamed(second)]));
    }

    [Fact]
    public void Two_frames_that_together_reach_exactly_the_limit_are_decoded()
    {
        var half = new byte[SixteenMiB / 2];

        Assert.Equal(SixteenMiB, ZstdRequestBody.Decode([.. ZstdFrames.OneShot(half), .. ZstdFrames.Streamed(half)]).Length);
    }

    [Fact]
    public void Two_frames_that_together_pass_the_limit_are_refused()
    {
        var half = new byte[SixteenMiB / 2 + 1];

        Assert.Throws<InvalidDataException>(() => ZstdRequestBody.Decode([.. ZstdFrames.OneShot(half), .. ZstdFrames.OneShot(half)]));
    }

    [Fact]
    public void A_streamed_frame_of_exactly_the_limit_is_decoded_though_its_bound_is_larger()
    {
        var encoded = ZstdFrames.Streamed(new byte[SixteenMiB]);
        Assert.True(ZstandardDecoder.TryGetMaxDecompressedLength(encoded, out var bound) && bound > SixteenMiB, "the bound must pass the limit");

        Assert.Equal(SixteenMiB, ZstdRequestBody.Decode(encoded).Length);
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

}
