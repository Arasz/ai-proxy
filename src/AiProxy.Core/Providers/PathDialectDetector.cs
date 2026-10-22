namespace AiProxy.Core.Providers;

/// <summary>Names the provider from the request path, which is the strongest signal there is.</summary>
/// <remarks>
/// The path is authoritative: <c>/v1/messages</c> is Anthropic and
/// <c>/v1/chat/completions</c> is OpenAI, whatever the body looks like.
/// </remarks>
public sealed class PathDialectDetector : IDialectDetector
{
    /// <summary>Paths that name a dialect outright.</summary>
    private static readonly (string Prefix, Dialect Dialect)[] PathRules =
    [
        ("/v1/messages", Dialect.Anthropic),
        ("/v1/chat/completions", Dialect.OpenAi),
        ("/v1/responses", Dialect.OpenAi),
        ("/v1/embeddings", Dialect.OpenAi),
        ("/v1/models", Dialect.OpenAi),
    ];

    /// <summary>Path prefixes whose shape is provider-specific but not exact.</summary>
    private static readonly (string Prefix, Dialect Dialect)[] PathPrefixRules =
    [
        ("/v1beta/models", Dialect.Gemini),
    ];

    /// <summary>Detects the dialect from the path alone.</summary>
    public Detection? Detect(DetectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dialect = DetectFromPath(request.Path);
        return dialect is null
            ? null
            : Detection.Resolved(dialect.Value, DialectSource.Path, $"path {request.Path} is a {dialect} endpoint");
    }

    private static Dialect? DetectFromPath(string path)
    {
        foreach (var (prefix, dialect) in PathRules)
        {
            if (path.Equals(prefix, StringComparison.Ordinal) ||
                path.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return dialect;
            }
        }

        foreach (var (prefix, dialect) in PathPrefixRules)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return dialect;
            }
        }

        return null;
    }
}
