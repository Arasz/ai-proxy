namespace AiProxy.Storage;

/// <summary>What the capture file holds now and how its last writes went.</summary>
/// <param name="LiveBytes">The bytes of the pages in use, not the size of the file.</param>
/// <param name="CapBytes">The live bytes the file may hold before capture evicts its oldest exchanges.</param>
/// <param name="OldestRowAt">When the oldest request row was captured, or null for an empty file.</param>
/// <param name="LastEvictionAt">When rows were last evicted, by the cap or by age, since the process started.</param>
/// <param name="LastWriteError">The reason the last write failed or was rejected, or null when none did.</param>
public sealed record CaptureState(long LiveBytes, long CapBytes, DateTimeOffset? OldestRowAt, DateTimeOffset? LastEvictionAt, string? LastWriteError);
