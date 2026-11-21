namespace AiProxy.Core.Content;

/// <summary>A tool call that asked for one whole file, and whether it lifted the read's line cap.</summary>
/// <param name="Path">The path the call named.</param>
/// <param name="AllowLarge">Whether the call asked for more than the default line cap.</param>
internal sealed record FileRead(string Path, bool AllowLarge)
{
    /// <summary>The line cap of a numbered read that did not ask for more.</summary>
    public const int LineCap = 2000;

    /// <summary>The path, when the result is the whole file; null when it may be a part of it.</summary>
    /// <param name="text">The result the call produced.</param>
    /// <remarks>
    ///     A result whose first line is numbered must number every line from one without a gap, and
    ///     stay under the cap unless the call lifted it. Any other line, such as a reminder or a
    ///     truncation notice, means the result is not the file alone. A result with no numbers is kept.
    /// </remarks>
    public string? PathFor(string text)
    {
        var lines = text.Split('\n');
        var count = lines.Length > 1 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        if (LineNumber.Of(lines[0]) is null)
        {
            return Path;
        }

        for (var index = 0; index < count; index++)
        {
            if (LineNumber.Of(lines[index]) != index + 1)
            {
                return null;
            }
        }

        return count < LineCap || AllowLarge ? Path : null;
    }
}
