using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiProxy.Tests.Integration;

/// <summary>The control pipe wire format, written independently of the production client.</summary>
internal static class ControlPipeClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What an owner answers to <c>info</c>, including the secret nonce.</summary>
    public sealed record Info(string Product, int Protocol, string Nonce, int Pid, DateTime StartUtc, string ProxyUrl, string[] ApiUrls, string? ProcessStartToken = null);

    public static string PipeName(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return $"aip-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32]}";
    }

    public static async Task<Info> ReadInfoAsync(string directory)
    {
        await using var pipe = await ConnectAsync(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteAsync(pipe, new { command = "info", protocol = 1 }, deadline.Token);
        return await ReadAsync<Info>(pipe, deadline.Token);
    }

    public static async Task StopAsync(string directory)
    {
        await using var pipe = await ConnectAsync(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await WriteAsync(pipe, new { command = "info", protocol = 1 }, deadline.Token);
        var info = await ReadAsync<Info>(pipe, deadline.Token);
        await WriteAsync(pipe, new { command = "stop", protocol = 1, nonce = info.Nonce }, deadline.Token);
        var reply = await ReadAsync<JsonElement>(pipe, deadline.Token);
        Assert.True(reply.GetProperty("accepted").GetBoolean());
    }

    public static async Task WriteAsync(Stream stream, object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        Assert.InRange(length, 1, 16384);
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, Json)!;
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string directory)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(deadline.Token);
        return pipe;
    }
}
