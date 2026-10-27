using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiProxy.Tests.Integration;

/// <summary>Real child processes own temporary directories and ephemeral loopback listeners.</summary>
public sealed class RestartTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Restart_without_an_owner_starts_an_owned_instance_with_actual_endpoints()
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, true);
        await child.ReadyAsync();
        var info = await ReadInfoAsync(directory.Path);
        Assert.Equal("ai-proxy", info.Product);
        Assert.Equal(1, info.Protocol);
        Assert.Equal(child.Process.Id, info.Pid);
        Assert.InRange(new Uri(info.ProxyUrl).Port, 1, 65535);
        using var http = new HttpClient();
        Assert.Contains("ok", await http.GetStringAsync(info.ApiUrls.Single() + "/health", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restart_waits_for_the_original_process_and_inherits_actual_endpoints()
    {
        using var directory = new TemporaryDirectory();
        await using var original = Child.Start(directory.Path, false);
        await original.ReadyAsync();
        var before = await ReadInfoAsync(directory.Path);
        await using var replacement = Child.Start(directory.Path, true, ephemeral: false);
        await replacement.ReadyAsync();
        Assert.True(original.Process.HasExited);
        Assert.Equal(0, original.Process.ExitCode);
        var after = await ReadInfoAsync(directory.Path);
        Assert.Equal(replacement.Process.Id, after.Pid);
        Assert.NotEqual(before.Nonce, after.Nonce);
        Assert.Equal(before.ProxyUrl, after.ProxyUrl);
        Assert.Equal(before.ApiUrls, after.ApiUrls);
        using var http = new HttpClient();
        Assert.Contains("ok", await http.GetStringAsync(after.ApiUrls.Single() + "/health", TestContext.Current.CancellationToken));
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var socket = await upstream.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(TestContext.Current.CancellationToken)))
            {
            }

            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\nforwarded"u8.ToArray(), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        using var proxied = new HttpClient(new HttpClientHandler { Proxy = new WebProxy(after.ProxyUrl), UseProxy = true });
        Assert.Equal("forwarded", await proxied.GetStringAsync($"http://127.0.0.1:{upstreamPort}/", TestContext.Current.CancellationToken));
        await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Restart_help_bypasses_invalid_configuration_and_creates_no_state()
    {
        using var directory = new TemporaryDirectory();
        await using var child = Child.Start(directory.Path, true, true, "--help", "--Proxy:Port", "invalid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await child.Process.WaitForExitAsync(deadline.Token);
        Assert.Equal(0, child.Process.ExitCode);
        Assert.Contains("--restart", child.Output);
        Assert.False(Directory.Exists(directory.Path));
    }

    [Fact]
    public async Task Held_legacy_marker_rejects_restart_before_touching_certificate_files()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var certificate = Path.Combine(directory.Path, "root-ca.pfx");
        var pem = Path.Combine(directory.Path, "root-ca.pem");
        await File.WriteAllTextAsync(certificate, "legacy private certificate bytes", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(pem, "legacy public certificate bytes", TestContext.Current.CancellationToken);
        var markerPath = Path.Combine(directory.Path, "system-proxy.lock");
        await File.WriteAllTextAsync(markerPath, "127.0.0.1:12345", TestContext.Current.CancellationToken);
        using (var marker = new FileStream(markerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await using var child = Child.Start(directory.Path, true);
            await child.ExitAsync();
            Assert.NotEqual(0, child.Process.ExitCode);
            Assert.Contains("Stop", child.Output);
            Assert.Contains("manually", child.Output);
        }

        Assert.Equal("legacy private certificate bytes", await File.ReadAllTextAsync(certificate, TestContext.Current.CancellationToken));
        Assert.Equal("legacy public certificate bytes", await File.ReadAllTextAsync(pem, TestContext.Current.CancellationToken));
        Assert.Equal("127.0.0.1:12345", await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Occupied_requested_endpoint_fails_before_certificates_and_leaves_the_listener_alive()
    {
        using var directory = new TemporaryDirectory();
        using var sentinel = new TcpListener(IPAddress.Loopback, 0);
        sentinel.Start();
        var port = ((IPEndPoint)sentinel.LocalEndpoint).Port;
        await using var child = Child.Start(directory.Path, true, true, $"--Proxy:Port={port}");
        await child.ExitAsync();
        Assert.NotEqual(0, child.Process.ExitCode);
        Assert.Contains("already in use", child.Output);
        Assert.Contains("manually", child.Output);
        Assert.False(File.Exists(Path.Combine(directory.Path, "root-ca.pfx")));
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        using var accepted = await sentinel.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        Assert.True(accepted.Connected);
    }

    private static string PipeName(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        return "aip-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    private static async Task<Info> ReadInfoAsync(string directory)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(deadline.Token);
        await WriteAsync(pipe, new { command = "info", protocol = 1 }, deadline.Token);
        return await ReadAsync<Info>(pipe, deadline.Token);
    }

    private static async Task WriteAsync(Stream stream, object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        Assert.InRange(length, 1, 16384);
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, Json)!;
    }

    private sealed record Info(string Product, int Protocol, string Nonce, int Pid, DateTime StartUtc, string ProxyUrl, string[] ApiUrls);

    private sealed class Child(Process process) : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _output = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Process Process => process;
        public string Output => string.Join("\n", _output);

        public static Child Start(string directory, bool restart, bool ephemeral = true, params string[] extra)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AiProxy.dll"));
            if (restart)
            {
                start.ArgumentList.Add("--restart");
            }

            foreach (var argument in extra)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["Proxy__DataDirectory"] = directory;
            start.Environment["Proxy__ConfigureSystemProxy"] = "false";
            start.Environment["Proxy__TrustRootCertificate"] = "false";
            start.Environment["Proxy__Compression__Enabled"] = "false";
            start.Environment["Proxy__Port"] = ephemeral ? "0" : null;
            start.Environment["ASPNETCORE_URLS"] = ephemeral ? "http://127.0.0.1:0" : null;
            var process = new Process { StartInfo = start };
            var child = new Child(process);
            process.OutputDataReceived += (_, args) => child.Record(args.Data);
            process.ErrorDataReceived += (_, args) => child.Record(args.Data);
            Assert.True(process.Start());
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return child;
        }

        private void Record(string? line)
        {
            if (line is null)
            {
                return;
            }

            _output.Enqueue(line);
            if (line.Contains("Application started", StringComparison.Ordinal))
            {
                _ready.TrySetResult();
            }
        }

        public async Task ExitAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token);
        }

        public async Task ReadyAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var exited = process.WaitForExitAsync(deadline.Token);
            var result = await Task.WhenAny(_ready.Task, exited).WaitAsync(deadline.Token);
            Assert.True(result == _ready.Task, Output);
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            process.Dispose();
        }
    }
}
