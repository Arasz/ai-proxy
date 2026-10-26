using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using AiProxy.Core.Json;

namespace AiProxy.Core.Capture;

/// <summary>Reads Anthropic token usage out of a provider response body as it arrived on the wire.</summary>
/// <remarks>Every <c>usage_error</c> value the capture writes is one of the constants here.</remarks>
public static class ResponseUsageReader
{
    /// <summary>The error when the body was not valid JSON, or a stream had no usage before a bad event.</summary>
    public const string Malformed = "malformed";

    /// <summary>The error when the body was readable and carried no Anthropic usage.</summary>
    public const string NoUsage = "no-usage";

    /// <summary>The error when the body did not decode with its content encoding, or was cut short inside it.</summary>
    public const string DecodeFailed = "decode-failed";

    /// <summary>The error when the body was larger than the proxy keeps for reading.</summary>
    public const string OverCap = "over-cap";

    /// <summary>The error when a stream ended inside an event; the usage read before it is kept.</summary>
    public const string Truncated = "truncated";

    /// <summary>The prefix of the error naming a content encoding the reader does not decode.</summary>
    public const string EncodingPrefix = "encoding:";

    /// <summary>The most bytes a body is decoded to; a body that decodes to more is <see cref="OverCap" />.</summary>
    public const int MaxDecodedBytes = 16 * 1024 * 1024;

    private static readonly byte[] DataField = "data:"u8.ToArray();

    /// <summary>Decodes the body and reads its usage; never throws on what the upstream sent.</summary>
    /// <param name="body">The body bytes, still content-encoded.</param>
    /// <param name="contentType">The response content type; <c>text/event-stream</c> is read as a stream of events.</param>
    /// <param name="contentEncoding">The response content encoding: gzip, deflate, br, identity or none.</param>
    public static UsageReading Read(ReadOnlyMemory<byte> body, string? contentType, string? contentEncoding)
    {
        var encoding = contentEncoding?.Trim().ToLowerInvariant();
        ReadOnlyMemory<byte> decoded;
        try
        {
            switch (encoding)
            {
                case null or "" or "identity":
                    decoded = body;
                    break;
                case "gzip" or "x-gzip":
                    decoded = DecodeGzip(body);
                    break;
                case "deflate":
                    decoded = DecodeDeflate(body);
                    break;
                case "br":
                    decoded = DecodeBrotli(body.Span);
                    break;
                default:
                    return new UsageReading(null, EncodingPrefix + encoding);
            }
        }
        catch (OverCapException)
        {
            return new UsageReading(null, OverCap);
        }
        catch (Exception)
        {
            // A decoder may throw any type on bytes it cannot read; all of them mean the same here.
            return new UsageReading(null, DecodeFailed);
        }

        try
        {
            return IsEventStream(contentType) ? ReadStream(decoded) : ReadMessage(decoded);
        }
        catch (JsonException)
        {
            return new UsageReading(null, Malformed);
        }
    }

    private static bool IsEventStream(string? contentType)
    {
        return contentType is not null && contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static UsageReading ReadMessage(ReadOnlyMemory<byte> body)
    {
        if (body.Span.Trim(" \t\r\n"u8).IsEmpty) return new UsageReading(null, NoUsage);

        using var document = JsonDocument.Parse(body);
        var usage = Merge(null, document.RootElement.PropertyOrDefault(UsageJson.Usage));
        return usage is null ? new UsageReading(null, NoUsage) : new UsageReading(usage, null);
    }

    /// <summary>Folds the usage of <c>message_start</c> and every <c>message_delta</c>; a later non-null count wins.</summary>
    private static UsageReading ReadStream(ReadOnlyMemory<byte> body)
    {
        ResponseUsage? usage = null;
        var broken = false;
        foreach (var line in Lines(body))
        {
            if (!line.Span.StartsWith(DataField)) continue;

            var data = line[DataField.Length..];
            if (data.Span.Trim(" "u8).SequenceEqual("[DONE]"u8)) continue;

            try
            {
                usage = Fold(usage, data);
            }
            catch (JsonException)
            {
                broken = true;
            }
        }

        return (usage, broken) switch
        {
            (null, true) => new UsageReading(null, Malformed),
            (null, false) => new UsageReading(null, NoUsage),
            (_, true) => new UsageReading(usage, Truncated),
            _ => new UsageReading(usage, null)
        };
    }

    private static ResponseUsage? Fold(ResponseUsage? usage, ReadOnlyMemory<byte> data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var type = root.PropertyOrDefault(UsageJson.Type);
        if (type.IsMissing) return usage;

        if (!type.IsString) throw new JsonException("An event type must be a string.");

        if (type.ValueEquals(UsageJson.MessageStart)) return Merge(usage, root.PropertyOrDefault(UsageJson.Message).PropertyOrDefault(UsageJson.Usage));

        return type.ValueEquals(UsageJson.MessageDelta)
            ? Merge(usage, root.PropertyOrDefault(UsageJson.Usage))
            : usage;
    }

    private static IEnumerable<ReadOnlyMemory<byte>> Lines(ReadOnlyMemory<byte> body)
    {
        var start = 0;
        while (start < body.Length)
        {
            var end = body.Span[start..].IndexOf((byte)'\n');
            end = end < 0 ? body.Length : start + end;
            var length = end - start;
            if (length > 0 && body.Span[end - 1] == (byte)'\r') length--;

            yield return body.Slice(start, length);
            start = end + 1;
        }
    }

    /// <summary>Takes the Anthropic counts of a usage object; one with none of them leaves <paramref name="current" /> as it was.</summary>
    private static ResponseUsage? Merge(ResponseUsage? current, JsonElement usage)
    {
        if (!usage.IsObject) return current;

        var input = usage.Int64Property(UsageJson.InputTokens);
        var output = usage.Int64Property(UsageJson.OutputTokens);
        var cacheCreation = usage.Int64Property(UsageJson.CacheCreationInputTokens);
        var cacheRead = usage.Int64Property(UsageJson.CacheReadInputTokens);
        if (input is null && output is null && cacheCreation is null && cacheRead is null) return current;

        return new ResponseUsage(
            input ?? current?.InputTokens,
            output ?? current?.OutputTokens,
            cacheCreation ?? current?.CacheCreationInputTokens,
            cacheRead ?? current?.CacheReadInputTokens);
    }

    /// <summary>A gzip body whose trailer does not hold the decoded length was cut short.</summary>
    private static ReadOnlyMemory<byte> DecodeGzip(ReadOnlyMemory<byte> body)
    {
        var decoded = Decode(body, static stream => new GZipStream(stream, CompressionMode.Decompress));
        if (body.Length < 18 || BinaryPrimitives.ReadUInt32LittleEndian(body.Span[^4..]) != (uint)decoded.Length) throw new InvalidDataException("The gzip trailer does not match the decoded body.");

        return decoded;
    }

    /// <summary>
    ///     HTTP deflate is meant to be zlib-wrapped, and its Adler-32 trailer shows a body cut short;
    ///     some servers send raw deflate, which carries no trailer, so a raw body cut short is read as far as it goes.
    /// </summary>
    private static ReadOnlyMemory<byte> DecodeDeflate(ReadOnlyMemory<byte> body)
    {
        ReadOnlyMemory<byte> decoded;
        try
        {
            decoded = Decode(body, static stream => new ZLibStream(stream, CompressionMode.Decompress));
        }
        catch (InvalidDataException)
        {
            return Decode(body, static stream => new DeflateStream(stream, CompressionMode.Decompress));
        }

        if (body.Length < 6 || BinaryPrimitives.ReadUInt32BigEndian(body.Span[^4..]) != Adler32(decoded.Span)) throw new InvalidDataException("The zlib trailer does not match the decoded body.");

        return decoded;
    }

    /// <summary>Decodes with the Brotli decoder directly, which says whether the input ended inside the stream.</summary>
    private static ReadOnlyMemory<byte> DecodeBrotli(ReadOnlySpan<byte> body)
    {
        using var decoder = new BrotliDecoder();
        var output = new ArrayBufferWriter<byte>(Math.Max(256, body.Length * 4));
        while (true)
        {
            var status = decoder.Decompress(body, output.GetSpan(64 * 1024), out var consumed, out var written);
            output.Advance(written);
            body = body[consumed..];
            if (output.WrittenCount > MaxDecodedBytes) throw new OverCapException();

            switch (status)
            {
                case OperationStatus.Done:
                    return output.WrittenMemory;
                case OperationStatus.DestinationTooSmall:
                    continue;
                default:
                    throw new InvalidDataException($"The Brotli stream ended with {status}.");
            }
        }
    }

    private static ReadOnlyMemory<byte> Decode(ReadOnlyMemory<byte> body, Func<Stream, Stream> decoder)
    {
        using var input = MemoryMarshal.TryGetArray(body, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, false)
            : new MemoryStream(body.ToArray(), false);
        using var stream = decoder(input);
        var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxDecodedBytes) throw new OverCapException();

            output.Write(buffer, 0, read);
        }

        return output.GetBuffer().AsMemory(0, (int)output.Length);
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint modulus = 65521;
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }

        return (b << 16) | a;
    }

    private sealed class OverCapException : Exception;
}