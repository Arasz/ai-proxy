namespace AiProxy.Core.Providers;

/// <summary>Names the provider from the request headers, which is the last resort.</summary>
/// <remarks>
/// A header names a provider the caller never addressed, so a header-detected
/// provider is treated like a body-detected one: the credential's provenance is no
/// better established, and the proxy substitutes its own credential.
/// </remarks>
public sealed class HeaderDialectDetector : IDialectDetector
{
    /// <summary>Detects the dialect from the headers alone.</summary>
    public Detection? Detect(DetectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dialect = DetectFromHeaders(request.Headers);
        return dialect is null
            ? null
            : Detection.Resolved(dialect.Value, DialectSource.Header, $"headers say {dialect}");
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
