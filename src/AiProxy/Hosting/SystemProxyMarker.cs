using System.Net;

namespace AiProxy.Hosting;

/// <summary>An exclusively held file that records an endpoint while the system proxy points at it.</summary>
public sealed class SystemProxyMarker : IDisposable
{
    /// <summary>File name inside <see cref="ProxyRuntimeOptions.DataDirectory"/>.</summary>
    public const string FileName = "system-proxy.lock";

    private readonly FileStream _stream;
    private readonly string _path;

    private SystemProxyMarker(FileStream stream, string path, bool previousRunDied)
    {
        _stream = stream;
        _path = path;
        PreviousRunDied = previousRunDied;
    }

    /// <summary>True when the file still named an endpoint, so an earlier run ended with the system proxy set.</summary>
    public bool PreviousRunDied { get; }

    /// <summary>Takes the marker in <paramref name="dataDirectory"/>; throws <see cref="InvalidOperationException"/> when another process holds it.</summary>
    public static SystemProxyMarker Acquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Another ai-proxy instance already owns the system proxy ({path} is in use). Stop it, then start ai-proxy again.",
                exception);
        }

        return new SystemProxyMarker(stream, path, stream.Length > 0);
    }

    /// <summary>Deletes the marker when no process holds it; returns false when it is held or missing.</summary>
    public static bool TryDelete(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
        }
        catch (IOException)
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>Replaces the file content with <paramref name="endPoint"/> as <c>host:port</c>.</summary>
    public void Write(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        _stream.SetLength(0);
        _stream.Position = 0;
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{endPoint.Address}:{endPoint.Port}");
        _stream.Write(bytes);
        _stream.Flush();
    }

    /// <summary>Empties and deletes the file, then releases the hold.</summary>
    public void Delete()
    {
        _stream.SetLength(0);
        _stream.Dispose();
        File.Delete(_path);
    }

    /// <summary>Releases the hold and leaves the file as it is.</summary>
    public void Dispose() => _stream.Dispose();
}
