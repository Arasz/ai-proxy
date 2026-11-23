using System.Buffers;
using System.IO.Compression;

namespace AiProxy.Hosting.Integration;

/// <summary>Decodes a request body sent with <c>Content-Encoding: zstd</c>, so it can be captured and measured as text.</summary>
internal static class ZstdRequestBody
{
    /// <summary>The most bytes a body is decoded to; a body that decodes to more is refused.</summary>
    public const int MaxDecodedBytes = 16 * 1024 * 1024;

    /// <summary>The largest window a frame may declare: 16 MiB, so a frame cannot make the decoder reserve more.</summary>
    public const int MaxWindowLog = 24;

    /// <summary>Whether a <c>Content-Encoding</c> value names zstd alone.</summary>
    public static bool IsZstd(string? contentEncoding) => contentEncoding is not null && contentEncoding.Trim().Equals("zstd", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the decoded bytes; throws <see cref="InvalidDataException" /> on a body that does not decode, is cut short or is too large.</summary>
    public static byte[] Decode(byte[] body)
    {
        if (body.Length == 0)
        {
            return [];
        }

        if (!ZstandardDecoder.TryGetMaxDecompressedLength(body, out var bound))
        {
            throw new InvalidDataException("The zstd body does not decode.");
        }

        // An empty frame reports a bound of 0, and the decoder needs room for at least one byte to finish it.
        var limit = (int)Math.Clamp(bound, 1, MaxDecodedBytes);
        var scratch = ArrayPool<byte>.Shared.Rent(limit);
        try
        {
            var written = Decompress(body, scratch.AsSpan(0, limit));
            return scratch.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Decodes every frame of the body into the destination and returns how many bytes it wrote.</summary>
    /// <remarks>The decoder stops after each frame and must be reset before the next one.</remarks>
    private static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        using var decoder = new ZstandardDecoder(MaxWindowLog);
        var written = 0;
        while (true)
        {
            OperationStatus status;
            int consumed;
            int count;
            try
            {
                status = decoder.Decompress(source, destination[written..], out consumed, out count);
            }
            catch (IOException exception)
            {
                // The decoder throws, rather than reporting InvalidData, when a frame needs a window above MaxWindowLog.
                throw new InvalidDataException("The zstd body does not decode.", exception);
            }

            source = source[consumed..];
            written += count;
            switch (status)
            {
                case OperationStatus.Done when source.IsEmpty:
                    return written;
                case OperationStatus.Done when consumed > 0:
                    decoder.Reset();
                    continue;
                case OperationStatus.DestinationTooSmall when destination.Length == MaxDecodedBytes:
                    throw new InvalidDataException($"The zstd body decodes to more than {MaxDecodedBytes} bytes.");
                default:
                    throw new InvalidDataException("The zstd body does not decode.");
            }
        }
    }
}
