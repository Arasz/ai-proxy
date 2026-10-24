using System.IO.Compression;
using System.Text.Json;

namespace AiProxy.Core.Capture;

/// <summary>Reads the token usage out of a provider response body as it arrived on the wire.</summary>
public static class ResponseUsageReader
{
    /// <summary>The error when the body was not valid JSON or a stream event was not.</summary>
    public const string Malformed = "malformed";

    /// <summary>The error when the body was readable and carried no usage.</summary>
    public const string NoUsage = "no-usage";

    /// <summary>The error when the body did not decode with its content encoding.</summary>
    public const string DecodeFailed = "decode-failed";

    private static readonly byte[] DataField = "data:"u8.ToArray();

    /// <summary>Decodes the body and reads its usage; never throws on what the upstream sent.</summary>
    /// <param name="body">The body bytes, still content-encoded.</param>
    /// <param name="contentType">The response content type; <c>text/event-stream</c> is read as a stream of events.</param>
    /// <param name="contentEncoding">The response content encoding: gzip, deflate, br, identity or none.</param>
    public static UsageReading Read(ReadOnlySpan<byte> body, string? contentType, string? contentEncoding)
    {
        var encoding = contentEncoding?.Trim().ToLowerInvariant();
        byte[] decoded;
        try
        {
            switch (encoding)
            {
                case null or "" or "identity":
                    decoded = body.ToArray();
                    break;
                case "gzip" or "x-gzip":
                    decoded = Decode(body, static stream => new GZipStream(stream, CompressionMode.Decompress));
                    break;
                case "deflate":
                    decoded = DecodeDeflate(body);
                    break;
                case "br":
                    decoded = Decode(body, static stream => new BrotliStream(stream, CompressionMode.Decompress));
                    break;
                default:
                    return new UsageReading(null, $"encoding:{encoding}");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return new UsageReading(null, DecodeFailed);
        }

        try
        {
            var usage = IsEventStream(contentType) ? ReadStream(decoded) : ReadMessage(decoded);
            return usage is null ? new UsageReading(null, NoUsage) : new UsageReading(usage, null);
        }
        catch (JsonException)
        {
            return new UsageReading(null, Malformed);
        }
    }

    private static bool IsEventStream(string? contentType) =>
        contentType is not null && contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

    private static ResponseUsage? ReadMessage(byte[] body)
    {
        if (body.AsSpan().Trim(" \t\r\n"u8).IsEmpty)
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("usage", out var usage)
            ? Merge(null, usage)
            : null;
    }

    /// <summary>Folds the usage of <c>message_start</c> and every <c>message_delta</c>; a later non-null count wins.</summary>
    private static ResponseUsage? ReadStream(byte[] body)
    {
        ResponseUsage? usage = null;
        foreach (var line in Lines(body))
        {
            if (!line.Span.StartsWith(DataField))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line[DataField.Length..]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type))
            {
                continue;
            }

            if (type.ValueEquals("message_start")
                && root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("usage", out var startUsage))
            {
                usage = Merge(usage, startUsage);
            }
            else if (type.ValueEquals("message_delta") && root.TryGetProperty("usage", out var deltaUsage))
            {
                usage = Merge(usage, deltaUsage);
            }
        }

        return usage;
    }

    private static IEnumerable<ReadOnlyMemory<byte>> Lines(byte[] body)
    {
        var start = 0;
        while (start < body.Length)
        {
            var end = Array.IndexOf(body, (byte)'\n', start);
            if (end < 0)
            {
                end = body.Length;
            }

            var length = end - start;
            if (length > 0 && body[end - 1] == (byte)'\r')
            {
                length--;
            }

            yield return body.AsMemory(start, length);
            start = end + 1;
        }
    }

    private static ResponseUsage? Merge(ResponseUsage? current, JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
        {
            return current;
        }

        return new ResponseUsage(
            Count(usage, "input_tokens") ?? current?.InputTokens,
            Count(usage, "output_tokens") ?? current?.OutputTokens,
            Count(usage, "cache_creation_input_tokens") ?? current?.CacheCreationInputTokens,
            Count(usage, "cache_read_input_tokens") ?? current?.CacheReadInputTokens);
    }

    private static long? Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count)
            ? count
            : null;

    private static byte[] Decode(ReadOnlySpan<byte> body, Func<Stream, Stream> decoder)
    {
        using var input = new MemoryStream(body.ToArray(), writable: false);
        using var stream = decoder(input);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>HTTP deflate is meant to be zlib-wrapped, and some servers send it raw; both are read.</summary>
    private static byte[] DecodeDeflate(ReadOnlySpan<byte> body)
    {
        try
        {
            return Decode(body, static stream => new ZLibStream(stream, CompressionMode.Decompress));
        }
        catch (InvalidDataException)
        {
            return Decode(body, static stream => new DeflateStream(stream, CompressionMode.Decompress));
        }
    }
}
