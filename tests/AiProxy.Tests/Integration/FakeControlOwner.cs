using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace AiProxy.Tests.Integration;

/// <summary>A scripted control-pipe server: answers <c>info</c> with <c>info</c> (or stalls when null), then answers <c>stop</c> with <c>acceptStop</c>.</summary>
internal sealed class FakeControlOwner : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly NamedPipeServerStream _server;
    private readonly Task _serving;
    private readonly ConcurrentQueue<string> _commands = new();

    public FakeControlOwner(string directory, object? info, bool acceptStop = false)
    {
        _server = new NamedPipeServerStream(ControlPipeClient.PipeName(directory), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _serving = Task.Run(() => ServeAsync(info, acceptStop));
    }

    /// <summary>The commands the clients sent, in order.</summary>
    public string[] Commands => [.. _commands];

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            await _serving;
        }
        catch (OperationCanceledException)
        {
        }

        await _server.DisposeAsync();
        _stopping.Dispose();
    }

    private async Task ServeAsync(object? info, bool acceptStop)
    {
        try
        {
            await _server.WaitForConnectionAsync(_stopping.Token);
            var request = await ControlPipeClient.ReadAsync<JsonElement>(_server, _stopping.Token);
            _commands.Enqueue(request.GetProperty("command").GetString()!);
            if (info is null)
            {
                await Task.Delay(Timeout.Infinite, _stopping.Token);
            }

            await ControlPipeClient.WriteAsync(_server, info!, _stopping.Token);
            var stop = await ControlPipeClient.ReadAsync<JsonElement>(_server, _stopping.Token);
            _commands.Enqueue(stop.GetProperty("command").GetString()!);
            await ControlPipeClient.WriteAsync(_server, new { accepted = acceptStop }, _stopping.Token);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            // The client left or the test ended.
        }
    }
}
