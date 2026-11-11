using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private MarkerRecord? _written;

    /// <summary>Holds the locked marker file and the record it held when taken.</summary>
    private SystemProxyMarker(FileStream stream, string path, MarkerRecord record)
    {
        _stream = stream;
        _path = path;
        PreviousEndpoint = record.Endpoint;
        PreviousSettingsSnapshot = record.PreviousSettingsSnapshot;
        AppliedSettingsSnapshot = record.AppliedSettingsSnapshot;
        DiscardedTornRecord = record.Torn;
    }

    /// <summary>The endpoint the file still named when it was taken, or null when it was empty.</summary>
    public string? PreviousEndpoint { get; }

    /// <summary>The serialized system proxy settings that preceded the previous run, when available.</summary>
    public string? PreviousSettingsSnapshot { get; }

    /// <summary>The serialized settings the previous run applied, when it recorded them after setting the proxy.</summary>
    public string? AppliedSettingsSnapshot { get; }

    /// <summary>True when the file held a record cut short by an interrupted write, which never preceded a system proxy change.</summary>
    public bool DiscardedTornRecord { get; }

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
                var marker = new SystemProxyMarker(stream, path, record);
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

    /// <summary>
    ///     Records the proxy endpoint and the system settings that must be restored after a crash, durably, before the proxy is set.
    ///     An interrupted write leaves a prefix of the JSON object, which the next owner discards as torn.
    /// </summary>
    public void Write(IPEndPoint endPoint, string? previousSettingsSnapshot = null)
    {
        var record = new MarkerRecord(endPoint.ToString(), previousSettingsSnapshot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        _stream.SetLength(0);
        _stream.Position = 0;
        _stream.Write(bytes);
        _stream.Flush(true);
        _written = record;
    }

    /// <summary>
    ///     Adds the settings ai-proxy applied to the record this hold wrote, without emptying the file first:
    ///     the new record extends the old one, so an interrupted write leaves the old record's fields readable.
    /// </summary>
    public void RecordApplied(string appliedSettingsSnapshot)
    {
        if (_written is null)
        {
            throw new InvalidOperationException("The system proxy marker has no record to add the applied settings to.");
        }

        var record = _written with { AppliedSettingsSnapshot = appliedSettingsSnapshot };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        _stream.Position = 0;
        _stream.Write(bytes);
        _stream.SetLength(bytes.Length);
        _stream.Flush(true);
        _written = record;
    }

    /// <summary>Empties the file and keeps the hold, recording that the system proxy no longer needs clearing.</summary>
    public void Truncate()
    {
        _written = null;
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

    /// <summary>Reads the record left by the previous holder, recovering endpoint-only, torn and partly extended records.</summary>
    private static MarkerRecord ReadRecord(FileStream stream)
    {
        if (stream.Length == 0)
        {
            return MarkerRecord.Empty;
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

            if (RecordBeforeApplied(text) is { } recorded)
            {
                return recorded;
            }

            // Write flushes the whole record before the system proxy is set, so a cut-short record never had a proxy change behind it.
            if (IsCutShort(text))
            {
                return MarkerRecord.Empty with { Torn = true };
            }

            throw new InvalidDataException("The system proxy marker is malformed.", exception);
        }
    }

    /// <summary>
    ///     True when the text is valid JSON that stops before its top-level object closes, wherever the cut fell
    ///     (a brace inside a string value included); text with an invalid token is malformed, not cut short.
    /// </summary>
    private static bool IsCutShort(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), isFinalBlock: false, state: default);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.CurrentDepth == 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Recovers the complete record a cut-short <see cref="RecordApplied" /> was extending: the text up to the applied field
    ///     (or a cut inside its name) must close into a record that already named its endpoint and previous settings.
    /// </summary>
    private static MarkerRecord? RecordBeforeApplied(string text)
    {
        const string AppliedField = ",\"appliedSettingsSnapshot\"";
        var start = text.IndexOf(AppliedField, StringComparison.Ordinal);
        if (start < 0)
        {
            start = Enumerable.Range(1, AppliedField.Length - 1)
                .Where(length => text.EndsWith(AppliedField[..length], StringComparison.Ordinal))
                .Select(length => text.Length - length)
                .DefaultIfEmpty(-1)
                .First();
        }

        if (start < 0)
        {
            return null;
        }

        try
        {
            var head = text[..start] + "}";
            using var document = JsonDocument.Parse(head);
            if (!document.RootElement.TryGetProperty("previousSettingsSnapshot", out _))
            {
                return null;
            }

            var record = document.Deserialize<MarkerRecord>(JsonOptions);
            return record?.Endpoint is { } endpoint && IPEndPoint.TryParse(endpoint, out _) && record.AppliedSettingsSnapshot is null ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Opens and locks the marker file, readable only by the current user.</summary>
    private static FileStream OpenMarker(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, LockShare);
            try
            {
                // The path-based call opens its own WRITE_DAC handle, which the held share mode does not block.
                new FileInfo(path).SetAccessControl(CurrentUserOnly());
                return windowsStream;
            }
            catch
            {
                windowsStream.Dispose();
                throw;
            }
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

    /// <summary>An access list that grants only the current Windows user.</summary>
    [SupportedOSPlatform("windows")]
    private static FileSecurity CurrentUserOnly()
    {
        var user = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("The current Windows user has no security identifier.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>The applied settings come last and are omitted until known, so recording them only extends the earlier record.</summary>
    private sealed record MarkerRecord(
        string? Endpoint,
        string? PreviousSettingsSnapshot,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AppliedSettingsSnapshot = null)
    {
        /// <summary>No record: the previous holder exited cleanly.</summary>
        public static MarkerRecord Empty { get; } = new(null, null);

        /// <summary>True when the record was cut short and discarded; never written to the file.</summary>
        [JsonIgnore]
        public bool Torn { get; init; }
    }
}
