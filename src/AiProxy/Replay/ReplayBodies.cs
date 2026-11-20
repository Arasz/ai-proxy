using AiProxy.Hosting.Integration;

namespace AiProxy.Replay;

/// <summary>Turns a stored body into the bytes the client sent.</summary>
public static class ReplayBodies
{
    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    /// <summary>Returns the decoded body, or null when it is zstd and does not decode.</summary>
    /// <param name="stored">The bytes the database holds.</param>
    public static byte[]? Decode(byte[] stored)
    {
        if (!stored.AsSpan().StartsWith(ZstdMagic))
        {
            return stored;
        }

        try
        {
            return ZstdRequestBody.Decode(stored);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
