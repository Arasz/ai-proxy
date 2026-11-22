using System.Runtime.InteropServices;

namespace AiProxy.Core.Capture;

/// <summary>Turns a body held as memory back into an array.</summary>
public static class BodyBytes
{
    /// <summary>The array behind the memory when the memory covers all of it, else a copy. The caller must not change the result.</summary>
    public static byte[] Array(ReadOnlyMemory<byte> body) =>
        MemoryMarshal.TryGetArray(body, out var segment) && segment is { Array: { } array, Offset: 0 } && segment.Count == array.Length
            ? array
            : body.ToArray();
}
