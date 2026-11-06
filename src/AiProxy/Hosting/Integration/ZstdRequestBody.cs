using ZstdSharp;
using ZstdSharp.Unsafe;

namespace AiProxy.Hosting.Integration;

/// <summary>Decodes a request body sent with <c>Content-Encoding: zstd</c>, so it can be captured and measured as text.</summary>
internal static class ZstdRequestBody
{
    /// <summary>The most bytes a body is decoded to; a body that decodes to more is refused.</summary>
    public const int MaxDecodedBytes = 16 * 1024 * 1024;

    /// <summary>The largest window a frame may declare: 16 MiB, so a frame cannot make the decoder reserve more.</summary>
    public const int MaxWindowLog = 24;

    /// <summary>Whether a <c>Content-Encoding</c> value names zstd alone.</summary>
    public static bool IsZstd(string? contentEncoding)
    {
        return contentEncoding is not null && contentEncoding.Trim().Equals("zstd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the decoded bytes; throws <see cref="InvalidDataException" /> on a body that does not decode, is cut short or is too large.</summary>
    public static byte[] Decode(byte[] body)
    {
        try
        {
            using var input = new MemoryStream(body, false);
            using var decoder = new DecompressionStream(input);
            decoder.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, MaxWindowLog);
            using var output = new MemoryStream(Math.Min(MaxDecodedBytes, Math.Max(body.Length, 1024) * 8));
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = decoder.Read(buffer)) > 0)
            {
                if (output.Length + read > MaxDecodedBytes)
                {
                    throw new InvalidDataException($"The zstd body decodes to more than {MaxDecodedBytes} bytes.");
                }

                output.Write(buffer, 0, read);
            }

            return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
        }
        catch (Exception exception) when (exception is ZstdException or EndOfStreamException)
        {
            throw new InvalidDataException("The zstd body does not decode.", exception);
        }
    }
}
