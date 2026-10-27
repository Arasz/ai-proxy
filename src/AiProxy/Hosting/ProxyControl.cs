using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiProxy.Hosting;

/// <summary>The same-user control connection for one leased proxy process.</summary>
internal sealed class ProxyControl(ProxyControl.Info info, IHostApplicationLifetime lifetime) : IAsyncDisposable
{
    internal const int Protocol = 1;
    internal const int MaximumFrameBytes = 16 * 1024;
    internal static readonly TimeSpan ConnectionDeadline = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _stopping = new();
    private NamedPipeServerStream? _pipe;
    private Task? _running;

    internal sealed record Info(string Product, int Protocol, string Nonce, int Pid, DateTime StartUtc, string ProxyUrl, string[] ApiUrls);
    internal sealed record Request(string Command, int Protocol, string? Nonce = null);
    internal sealed record Reply(bool Accepted);

    internal static string PipeName(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return "aip-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    internal void Start(string directory)
    {
        // Construction can unlink an old Unix socket, so the caller must already hold the process lease.
        _pipe = new NamedPipeServerStream(PipeName(directory), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _running = ServeAsync(_pipe);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                deadline.CancelAfter(ConnectionDeadline);
                var request = await ReadAsync<Request>(pipe, deadline.Token).ConfigureAwait(false);
                if (request.Command == "info" && request.Protocol == Protocol)
                {
                    await WriteAsync(pipe, info, deadline.Token).ConfigureAwait(false);
                    var stop = await ReadAsync<Request>(pipe, deadline.Token).ConfigureAwait(false);
                    var accepted = stop.Command == "stop" && stop.Protocol == Protocol && stop.Nonce == info.Nonce;
                    await WriteAsync(pipe, new Reply(accepted), deadline.Token).ConfigureAwait(false);
                    if (accepted)
                    {
                        lifetime.StopApplication();
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException)
            {
                // A disconnected, malformed or stalled peer loses only its own connection.
            }
            finally
            {
                if (pipe.IsConnected)
                {
                    pipe.Disconnect();
                }
            }
        }
    }

    internal static async Task<Info> StopOwnedAsync(string directory, CancellationToken token)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ConnectionDeadline);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        await WriteAsync(pipe, new Request("info", Protocol), deadline.Token).ConfigureAwait(false);
        var owner = await ReadAsync<Info>(pipe, deadline.Token).ConfigureAwait(false);
        if (owner.Product != "ai-proxy" || owner.Protocol != Protocol || string.IsNullOrWhiteSpace(owner.Nonce) ||
            owner.Pid <= 0 || owner.Pid == Environment.ProcessId || owner.StartUtc.Kind != DateTimeKind.Utc ||
            !Uri.TryCreate(owner.ProxyUrl, UriKind.Absolute, out var proxy) || proxy.Scheme != "http" ||
            owner.ApiUrls is not { Length: > 0 } || owner.ApiUrls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out _)))
        {
            throw new IOException("The control peer is not a compatible ai-proxy owner. Stop it manually before starting again.");
        }

        using var process = Process.GetProcessById(owner.Pid);
        if (process.StartTime.ToUniversalTime() != owner.StartUtc)
        {
            throw new IOException("The ai-proxy owner identity changed. Stop it manually before starting again.");
        }

        await WriteAsync(pipe, new Request("stop", Protocol, owner.Nonce), deadline.Token).ConfigureAwait(false);
        var reply = await ReadAsync<Reply>(pipe, deadline.Token).ConfigureAwait(false);
        if (!reply.Accepted)
        {
            throw new IOException("The ai-proxy owner refused shutdown. Stop it manually before starting again.");
        }

        // An acknowledgement only means shutdown was requested; final disposal must finish before replacement.
        using var exitDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        exitDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
        return owner;
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
        {
            throw new IOException("The ai-proxy control frame exceeds its byte limit.");
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new JsonException("The control frame is empty.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumFrameBytes)
        {
            throw new IOException("The ai-proxy control frame exceeds its byte limit.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_running is not null)
        {
            await _running.ConfigureAwait(false);
        }

        _pipe?.Dispose();
        _stopping.Dispose();
    }
}
