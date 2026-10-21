using System.Text.Json;

namespace AiProxy.Core.Providers;

/// <summary>
/// Infers which provider dialect a request speaks, from the path, the headers and the
/// body shape, in that order of authority.
/// </summary>
/// <remarks>
/// The path is authoritative when it names a known endpoint: <c>/v1/messages</c> is
/// Anthropic and <c>/v1/chat/completions</c> is OpenAI, whatever the body looks like.
/// The body shape is consulted only when the path is generic, and the headers are the
/// last resort. A path and a body that name different providers yield
/// <see cref="Dialect.Unknown"/> rather than a guess: a request whose provider was
/// guessed carries a credential of unknown provenance, and the proxy must not send it
/// to a host it chose.
/// </remarks>
public static class DialectDetector
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

    /// <summary>What the detector concluded, and the evidence behind it.</summary>
    /// <param name="Dialect">The detected dialect, or <see cref="Dialect.Unknown"/>.</param>
    /// <param name="Source">Which signal named the provider.</param>
    /// <param name="Reason">The human-readable evidence trail.</param>
    public sealed record Detection(Dialect Dialect, DialectSource Source, string Reason)
    {
        /// <summary>
        /// Whether the caller's own credential may be forwarded with this request.
        /// </summary>
        /// <remarks>
        /// True only when the path named the provider, because then the caller addressed
        /// that provider's endpoint and the credential is theirs. A body or a header names
        /// a provider the caller never addressed, so the credential's provenance is
        /// unknown — the research record's F4 rule requires the proxy to substitute its
        /// own credential in that case, or refuse the request.
        /// </remarks>
        public bool CredentialBelongsToCaller => Source == DialectSource.Path;
    }

    /// <summary>Detects the dialect of a request.</summary>
    /// <param name="path">The request path, without a query string.</param>
    /// <param name="headers">The request headers, keyed case-insensitively.</param>
    /// <param name="body">The request body, or null when there is none.</param>
    public static Detection Detect(string path, IReadOnlyDictionary<string, string> headers, string? body)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(headers);

        var pathDialect = DetectFromPath(path);
        var bodyDialect = DetectFromBody(body);
        var headerDialect = DetectFromHeaders(headers);

        if (pathDialect is not null && bodyDialect is not null && pathDialect != bodyDialect)
        {
            return new Detection(
                Dialect.Unknown,
                DialectSource.None,
                $"path says {pathDialect} ({path}) but body says {bodyDialect}; refusing to guess");
        }

        if (pathDialect is not null)
        {
            return new Detection(
                pathDialect.Value,
                DialectSource.Path,
                $"path {path} is a {pathDialect} endpoint");
        }

        if (bodyDialect is not null)
        {
            var agrees = headerDialect == bodyDialect;
            return new Detection(
                bodyDialect.Value,
                DialectSource.Body,
                agrees
                    ? $"body shape is {bodyDialect}; headers agree"
                    : $"body shape is {bodyDialect}");
        }

        if (headerDialect is not null)
        {
            return new Detection(headerDialect.Value, DialectSource.Header, $"headers say {headerDialect}");
        }

        return new Detection(
            Dialect.Unknown,
            DialectSource.None,
            $"no dialect signal in path {path}, headers or body");
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

    private static Dialect? DetectFromBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("contents", out _))
            {
                return Dialect.Gemini;
            }

            var anthropic = root.TryGetProperty("max_tokens", out _) ||
                            root.TryGetProperty("thinking", out _) ||
                            root.TryGetProperty("anthropic_version", out _) ||
                            HasCacheControl(root);
            var openAi = root.TryGetProperty("max_completion_tokens", out _) ||
                         root.TryGetProperty("response_format", out _) ||
                         root.TryGetProperty("reasoning_effort", out _) ||
                         root.TryGetProperty("stream_options", out _);

            if (anthropic && !openAi)
            {
                return Dialect.Anthropic;
            }

            if (openAi && !anthropic)
            {
                return Dialect.OpenAi;
            }

            return null;
        }
    }

    private static bool HasCacheControl(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("cache_control", out _))
            {
                return true;
            }
        }

        return false;
    }

    private static Dialect? DetectFromHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.ContainsKey("anthropic-version"))
        {
            return Dialect.Anthropic;
        }

        if (headers.ContainsKey("x-goog-api-key"))
        {
            return Dialect.Gemini;
        }

        if (headers.TryGetValue("user-agent", out var userAgent) &&
            userAgent.Contains("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return Dialect.Anthropic;
        }

        return null;
    }
}
