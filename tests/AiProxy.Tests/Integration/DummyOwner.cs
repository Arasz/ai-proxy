using System.Diagnostics;
using AiProxy.Hosting.Proxy;

namespace AiProxy.Tests.Integration;

/// <summary>A long-lived idle child process that stands in for an owner's pid; killed on dispose.</summary>
internal sealed class DummyOwner : IDisposable
{
    private readonly Process _process;

    public DummyOwner()
    {
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("ping", "-n 600 127.0.0.1") : new ProcessStartInfo("sleep", "600");
        start.RedirectStandardOutput = true;
        start.UseShellExecute = false;
        _process = Process.Start(start)!;
    }

    public bool Alive => !_process.HasExited;

    /// <summary>What an owner at this pid would report; a wrong start token models a reused pid.</summary>
    public ControlPipeClient.Info Info(bool validIdentity = true)
    {
        var token = validIdentity ? new ProcessIdentity(_process).StartToken : "native:0";
        return new ControlPipeClient.Info("ai-proxy", 1, "fake-nonce", _process.Id, _process.StartTime.ToUniversalTime(),
            "http://127.0.0.1:1", ["http://127.0.0.1:2"], token);
    }

    public Task EndAsync()
    {
        _process.Kill(true);
        return _process.WaitForExitAsync();
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(true);
        }

        _process.Dispose();
    }
}
