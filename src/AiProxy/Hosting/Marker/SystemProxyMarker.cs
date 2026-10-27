using System.Net;
using System.Text;
using AiProxy.Hosting.Proxy;

namespace AiProxy.Hosting.Marker;

/// <summary>An exclusively held file that records an endpoint while the system proxy points at it.</summary>
public sealed class SystemProxyMarker : IDisposable
{
    /// <summary>File name inside <see cref="ProxyRuntimeOptions.DataDirectory" />.</summary>
    internal const string FileName = "system-proxy.lock";

    /// <summary>
    /// Unix takes a flock only for FileShare.None.
    /// Windows needs FileShare.Delete so the owner can delete the file it holds;
    /// other openers still conflict because their own share mode excludes our read/write access.
    /// </summary>
    private static readonly FileShare LockShare = OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None;
    private readonly string _path;
    private readonly FileStream _stream;

    private SystemProxyMarker(FileStream stream, string path, string? previousEndpoint)
    {
        _stream = stream;
        _path = path;
        PreviousEndpoint = previousEndpoint;
    }

    /// <summary>The endpoint the file still named when it was taken, or null when it was empty.</summary>
    public string? PreviousEndpoint { get; }

    /// <summary>True when the file still named an endpoint, so an earlier run ended with the system proxy set.</summary>
    public bool PreviousRunDied => PreviousEndpoint is not null;

    /// <summary>Releases the hold and leaves the file as it is.</summary>
    public void Dispose() => _stream.Dispose();

    /// <summary>
    ///     Takes the marker in <paramref name="dataDirectory" />, retrying for <paramref name="wait" /> while another process holds it;
    ///     throws <see cref="InvalidOperationException" /> when it is still held.
    /// </summary>
    public static async Task<SystemProxyMarker> Acquire(string dataDirectory, TimeSpan wait, TimeProvider timeProvider)
    {
        var path = Path.Combine(dataDirectory, FileName);
        var deadline = timeProvider.GetUtcNow() + wait;
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, LockShare);
                return new SystemProxyMarker(stream, path, ReadEndpoint(stream));
            }
            catch (IOException exception)
            {
                if (timeProvider.GetUtcNow() >= deadline)
                {
                    throw new InvalidOperationException(
                        $"Another ai-proxy instance already owns the system proxy ({path} is in use). Stop it, then start ai-proxy again.",
                        exception);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50 + Random.Shared.Next(0, 50)), timeProvider);
            }
        }
    }

    /// <summary>Replaces the file content with <paramref name="endPoint" /> as <c>host:port</c>.</summary>
    public void Write(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        _stream.SetLength(0);
        _stream.Position = 0;
        _stream.Write(Encoding.UTF8.GetBytes($"{endPoint.Address}:{endPoint.Port}"));
        _stream.Flush();
    }

    /// <summary>Empties the file and keeps the hold, recording that the system proxy no longer needs clearing.</summary>
    public void Truncate()
    {
        _stream.SetLength(0);
        _stream.Flush();
    }

    /// <summary>Empties and deletes the file while still holding it, then releases the hold.</summary>
    public void Delete()
    {
        _stream.SetLength(0);
        File.Delete(_path);
        _stream.Dispose();
    }

    private static string? ReadEndpoint(FileStream stream)
    {
        if (stream.Length == 0)
        {
            return null;
        }

        var text = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true).ReadToEnd().Trim();
        return text.Length == 0 ? "unknown" : text;
    }
}
