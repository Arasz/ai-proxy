using System.Net;
using System.Text;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly FileStream _stream;

    private SystemProxyMarker(FileStream stream, string path, string? previousEndpoint, string? previousSettingsSnapshot)
    {
        _stream = stream;
        _path = path;
        PreviousEndpoint = previousEndpoint;
        PreviousSettingsSnapshot = previousSettingsSnapshot;
    }

    /// <summary>The endpoint the file still named when it was taken, or null when it was empty.</summary>
    public string? PreviousEndpoint { get; }

    /// <summary>The serialized system proxy settings that preceded the previous run, when available.</summary>
    public string? PreviousSettingsSnapshot { get; }

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
            FileStream? stream = null;
            try
            {
                stream = OpenMarker(path);
                var record = ReadRecord(stream);
                var marker = new SystemProxyMarker(stream, path, record.Endpoint, record.PreviousSettingsSnapshot);
                stream = null;
                return marker;
            }
            catch (IOException exception)
            {
                stream?.Dispose();
                if (timeProvider.GetUtcNow() >= deadline)
                {
                    throw new InvalidOperationException(
                        $"Another ai-proxy instance already owns the system proxy ({path} is in use). Stop it, then start ai-proxy again.",
                        exception);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50 + Random.Shared.Next(0, 50)), timeProvider);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }
    }

    /// <summary>Records the proxy endpoint and the system settings that must be restored after a crash.</summary>
    public void Write(IPEndPoint endPoint, string? previousSettingsSnapshot = null)
    {
        _stream.SetLength(0);
        _stream.Position = 0;
        var record = new MarkerRecord(endPoint.ToString(), previousSettingsSnapshot);
        JsonSerializer.Serialize(_stream, record, JsonOptions);
        _stream.Flush(true);
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

    private static MarkerRecord ReadRecord(FileStream stream)
    {
        if (stream.Length == 0)
        {
            return new MarkerRecord(null, null);
        }

        var text = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true).ReadToEnd().Trim();
        if (text.Length == 0)
        {
            throw new InvalidDataException("The system proxy marker contains no valid record.");
        }

        try
        {
            var record = JsonSerializer.Deserialize<MarkerRecord>(text, JsonOptions);
            if (record?.Endpoint is null || !IPEndPoint.TryParse(record.Endpoint, out _))
            {
                throw new InvalidDataException("The system proxy marker contains an invalid endpoint.");
            }

            return record;
        }
        catch (JsonException exception)
        {
            // Older versions wrote only the endpoint. Keep recognizing that marker so upgrades still recover.
            if (IPEndPoint.TryParse(text, out _))
            {
                return new MarkerRecord(text, null);
            }

            throw new InvalidDataException("The system proxy marker is malformed.", exception);
        }
    }

    private static FileStream OpenMarker(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, LockShare);
        }

        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = LockShare,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private sealed record MarkerRecord(string? Endpoint, string? PreviousSettingsSnapshot);
}
