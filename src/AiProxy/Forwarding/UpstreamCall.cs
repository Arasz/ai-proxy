namespace AiProxy.Forwarding;

/// <summary>One request on its way to an upstream.</summary>
public sealed record UpstreamCall
{
    /// <summary>The upstream the request goes to.</summary>
    public required Uri Upstream { get; init; }

    /// <summary>The path and the query string, unchanged.</summary>
    public required string PathAndQuery { get; init; }

    /// <summary>The caller's method, unchanged.</summary>
    public required string Method { get; init; }

    /// <summary>The caller's headers, before the hop-by-hop set is dropped.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>The body, when it was read into memory.</summary>
    public ReadOnlyMemory<byte>? Body { get; init; }

    /// <summary>The body as a stream, when it was not read into memory.</summary>
    public Stream? BodyStream { get; init; }

    /// <summary>The content type of the body.</summary>
    public string? ContentType { get; init; }

}
