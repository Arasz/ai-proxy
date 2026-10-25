namespace AiProxy.Core.Content;

/// <summary>Bytes that take the place of a stretch of the request body.</summary>
/// <param name="Start">The offset of the stretch in the body.</param>
/// <param name="Length">The length of the stretch in bytes.</param>
/// <param name="Bytes">The bytes that replace it.</param>
internal sealed record Replacement(int Start, int Length, byte[] Bytes);
