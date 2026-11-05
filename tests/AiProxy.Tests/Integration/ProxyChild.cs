using System.Collections.Concurrent;
using System.Diagnostics;

namespace AiProxy.Tests.Integration;

/// <summary>A real ai-proxy child process with its own data directory; kills the tree on dispose.</summary>
internal sealed class ProxyChild(Process process) : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _output = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Process Process => process;
    public string Output => string.Join("\n", _output);

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(true);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(deadline.Token);
        process.Dispose();
    }

    /// <summary>Starts the proxy, optionally with <c>--restart</c> and extra arguments.</summary>
    public static ProxyChild Start(string directory, bool restart, bool ephemeral = true, params string[] extra)
    {
        return StartWith(directory, [.. restart ? ["--restart"] : Array.Empty<string>(), .. extra], ephemeral);
    }

    /// <summary>Starts the executable with exactly <paramref name="arguments" /> and an isolated environment.</summary>
    public static ProxyChild StartWith(string directory, string[] arguments, bool ephemeral = true, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AiProxy.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // A throwaway home, so a child whose data-directory setting is missing or ignored
        // resolves ~/.ai-proxy under it and can never reach the developer's running proxy.
        var home = Path.Combine(Path.GetTempPath(), "ai-proxy-child-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        start.Environment["HOME"] = home;
        start.Environment["USERPROFILE"] = home;
        start.Environment["Proxy__DataDirectory"] = directory;
        start.Environment["Proxy__ConfigureSystemProxy"] = "false";
        start.Environment["Proxy__TrustRootCertificate"] = "false";
        start.Environment["Proxy__Compression__Enabled"] = "false";
        start.Environment["Proxy__Port"] = ephemeral ? "0" : null;
        start.Environment["ASPNETCORE_URLS"] = ephemeral ? "http://127.0.0.1:0" : null;
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            start.Environment[name] = value;
        }

        var process = new Process { StartInfo = start };
        var child = new ProxyChild(process);
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
}
