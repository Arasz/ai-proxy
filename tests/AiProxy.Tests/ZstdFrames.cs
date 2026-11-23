using System.IO.Compression;

namespace AiProxy.Tests;

/// <summary>Builds zstd bodies for tests with the .NET encoder.</summary>
internal static class ZstdFrames
{
    /// <summary>One frame that declares its decoded size.</summary>
    public static byte[] OneShot(byte[] bytes)
    {
        var encoded = new byte[ZstandardEncoder.GetMaxCompressedLength(bytes.Length)];
        if (!ZstandardEncoder.TryCompress(bytes, encoded, out var written))
        {
            throw new InvalidOperationException("The test body did not compress.");
        }

        return encoded[..written];
    }

    /// <summary>One frame written as a stream in two flushes, so it does not declare its decoded size.</summary>
    public static byte[] Streamed(byte[] bytes, int windowLog2 = 0)
    {
        var options = new ZstandardCompressionOptions { Quality = 3 };
        if (windowLog2 > 0)
        {
            options.WindowLog2 = windowLog2;
        }

        using var output = new MemoryStream();
        using (var encoder = new ZstandardStream(output, options, leaveOpen: true))
        {
            var half = bytes.Length / 2;
            encoder.Write(bytes, 0, half);
            encoder.Flush();
            encoder.Write(bytes, half, bytes.Length - half);
        }

        return output.ToArray();
    }
}
