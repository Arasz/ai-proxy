using System.Text.Json;

namespace AiProxy.Core.Providers;

/// <summary>Names the provider from the shape of the request body.</summary>
/// <remarks>
/// The body is consulted only when the path names no provider, and it never overrides
/// a path that does.
/// </remarks>
public sealed class BodyDialectDetector : IDialectDetector
{
    /// <summary>Detects the dialect from the body shape alone.</summary>
    public Detection? Detect(DetectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dialect = DetectFromBody(request.Body);
        return dialect is null
            ? null
            : Detection.Resolved(dialect.Value, DialectSource.Body, $"body shape is {dialect}");
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
}
