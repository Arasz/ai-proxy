using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiProxy.Hosting.Proxy;

/// <summary>The same-user control connection for one leased proxy process.</summary>
internal sealed class ProxyControl(string directory, ProxyControl.ProxyInfo proxyInfo, IHostApplicationLifetime lifetime) : IAsyncDisposable
{
    internal const int DefaultProtocol = 1;
    private static readonly TimeSpan ConnectionDeadline = TimeSpan.FromSeconds(3);

    // Binding may unlink a stale Unix socket; the caller already holds both leases.
    private readonly NamedPipeServerStream _pipe = new(PipeName(directory), PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _running;

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_running is not null)
        {
            await _running;
        }

        await _pipe.DisposeAsync();
        _stopping.Dispose();
    }

    private static string PipeName(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return $"aip-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32]}";
    }

    internal async ValueTask Start(StartupLease startupLease)
    {
        await startupLease.DisposeAsync();
        _running = ServeAsync(_pipe);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_stopping.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                deadline.CancelAfter(ConnectionDeadline);
                var request = await pipe.ReadAsync<Request>(deadline.Token);
                if (request is not { Command: "info", Protocol: DefaultProtocol })
                {
                    continue;
                }

                await HandleStopCommandAsync(pipe, deadline);
            }
            catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException)
            {
                // A disconnected, malformed or stalled peer loses only its own connection.
            }
            finally
            {
                try
                {
                    pipe.Disconnect();
                }
                catch (InvalidOperationException)
                {
                    // The pipe is already disconnected or closed.
                }
            }
        }
    }

    private async Task HandleStopCommandAsync(NamedPipeServerStream pipe, CancellationTokenSource deadline)
    {
        await pipe.WriteAsync(proxyInfo, deadline.Token);
        var stop = await pipe.ReadAsync<Request>(deadline.Token);
        var accepted = stop is { Command: "stop", Protocol: DefaultProtocol } && stop.Nonce == proxyInfo.Nonce;
        await pipe.WriteAsync(new Reply(accepted), deadline.Token);
        if (accepted)
        {
            lifetime.StopApplication();
        }
    }

    internal static async Task<ProxyInfo> StopOwnedAsync(string directory, CancellationToken token, Action<ProxyInfo>? beforeStop = null)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ConnectionDeadline);

        await pipe.ConnectAsync(deadline.Token);

        await pipe.WriteAsync(new Request("info", DefaultProtocol), deadline.Token);

        var owner = await pipe.ReadAsync<ProxyInfo>(deadline.Token);

        EnsureProxyOwnerIsCompatible(owner);

        using var process = Process.GetProcessById(owner.Pid);

        EnsureProcessIdentityIntegrity(process, owner);

        beforeStop?.Invoke(owner);
        return await RequestShutdownAsync(owner, process, pipe, deadline, token);
    }

    private static async Task<ProxyInfo> RequestShutdownAsync(ProxyInfo owner, Process process, NamedPipeClientStream pipe, CancellationTokenSource deadline, CancellationToken token)
    {
        await pipe.WriteAsync(new Request("stop", DefaultProtocol, owner.Nonce), deadline.Token);
        var reply = await pipe.ReadAsync<Reply>(deadline.Token);
        if (!reply.Accepted)
        {
            throw new IOException("The ai-proxy owner refused shutdown. Stop it manually before starting again.");
        }

        // An acknowledgement only means shutdown was requested; final disposal must finish before replacement.
        using var exitDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        exitDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(exitDeadline.Token);
        return owner;
    }

    private static void EnsureProcessIdentityIntegrity(Process process, ProxyInfo owner)
    {
        var processIdentity = new ProcessIdentity(process);
        if (processIdentity.StartToken != owner.ProcessStartToken || !OperatingSystem.IsLinux() && process.StartTime.ToUniversalTime() != owner.StartUtc)
        {
            throw new IOException("The ai-proxy owner identity changed. Stop it manually before starting again.");
        }
    }

    private static void EnsureProxyOwnerIsCompatible(ProxyInfo owner)
    {
        if (owner.Product != "ai-proxy" || owner.Protocol != DefaultProtocol || string.IsNullOrWhiteSpace(owner.Nonce) ||
            owner.Pid <= 0 || owner.Pid == Environment.ProcessId || owner.StartUtc.Kind != DateTimeKind.Utc ||
            string.IsNullOrWhiteSpace(owner.ProcessStartToken) ||
            !Uri.TryCreate(owner.ProxyUrl, UriKind.Absolute, out var proxy) || proxy.Scheme != "http" ||
            owner.ApiUrls is not { Length: > 0 } || owner.ApiUrls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out _)))
        {
            throw new IOException("The control peer is not a compatible ai-proxy owner. Stop it manually before starting again.");
        }
    }

    internal sealed record ProxyInfo(string Product, int Protocol, string Nonce, int Pid, DateTime StartUtc, string ProxyUrl, string[] ApiUrls, string ProcessStartToken);

    private sealed record Request(string Command, int Protocol, string? Nonce = null);

    private sealed record Reply(bool Accepted);
}

file static class PipeStreamExtensions
{
    private const int MaximumFrameBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    extension(Stream stream)
    {
        public async Task<T> ReadAsync<T>(CancellationToken token)
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, token);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length is <= 0 or > MaximumFrameBytes)
            {
                throw new IOException("The ai-proxy control frame exceeds its byte limit.");
            }

            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, token);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new JsonException("The control frame is empty.");
        }

        public async Task WriteAsync<T>(T value, CancellationToken token)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (bytes.Length > MaximumFrameBytes)
            {
                throw new IOException("The ai-proxy control frame exceeds its byte limit.");
            }

            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
        }
    }
}
