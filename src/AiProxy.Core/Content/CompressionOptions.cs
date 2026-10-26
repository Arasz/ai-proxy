namespace AiProxy.Core.Content;

/// <summary>What the operator configured about the compression stage.</summary>
/// <param name="MinimumLengths">The shortest part of each kind that is worth compressing.</param>
public sealed record CompressionOptions(IReadOnlyDictionary<ContentType, int> MinimumLengths)
{
    /// <summary>The shortest part of a kind that is worth compressing, when the operator set none.</summary>
    public const int DefaultMinimum = 512;

    /// <summary>The options the stage runs with when the operator sets none.</summary>
    public static CompressionOptions Default { get; } = new(new Dictionary<ContentType, int>
    {
        [ContentType.Log] = 200,
        [ContentType.Json] = 100,
        [ContentType.Xml] = 100,
        [ContentType.Toml] = 100,
        [ContentType.Markdown] = 200,
        [ContentType.SourceCode] = 500,
        [ContentType.Prose] = DefaultMinimum,
        [ContentType.DirectoryListing] = int.MaxValue,
        [ContentType.ShellTranscript] = 200
    });

    /// <summary>The options that use one minimum for every kind.</summary>
    /// <param name="minimum">The smallest part that is worth compressing.</param>
    public static CompressionOptions WithMinimum(int minimum)
    {
        return new CompressionOptions(
            Default.MinimumLengths.ToDictionary(
                pair => pair.Key,
                pair => pair.Key == ContentType.DirectoryListing ? int.MaxValue : minimum));
    }

    /// <summary>The shortest part of the kind that is worth compressing.</summary>
    /// <param name="type">The kind of part.</param>
    public int MinimumFor(ContentType type)
    {
        return MinimumLengths.GetValueOrDefault(type, DefaultMinimum);
    }
}