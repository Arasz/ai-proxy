using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace AiProxy.Hosting.Proxy;

/// <summary>Writes every log event to a file that rolls once at a fixed size, so two files are the whole history.</summary>
/// <remarks>Events queue on a bounded channel and one background task writes them, so a caller never waits on the disk.</remarks>
internal sealed class ProxyLogFile : ILoggerProvider
{
    public const string FileName = "proxy.log";
    public const string RolledFileName = "proxy.1.log";
    public const long DefaultMaxBytes = 5 * 1024 * 1024;

    private const int QueueCapacity = 8192;
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _path;
    private readonly string _rolledPath;
    private readonly long _maxBytes;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly Task _writer;

    /// <summary>Opens <paramref name="path" /> for append and rolls it to <c>proxy.1.log</c> beside it at <paramref name="maxBytes" />.</summary>
    public ProxyLogFile(string path, long maxBytes)
    {
        _path = path;
        _rolledPath = Path.Combine(Path.GetDirectoryName(path)!, RolledFileName);
        _maxBytes = maxBytes;
        _writer = Task.Run(WriteAsync);
    }

    /// <summary>The provider for the log file inside a data directory.</summary>
    public static ProxyLogFile ForDirectory(string directory) => new(Path.Combine(directory, FileName), DefaultMaxBytes);

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _queue.Writer);

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.GetAwaiter().GetResult();
    }

    private async Task WriteAsync()
    {
        FileStream? stream = null;
        try
        {
            stream = Open();
            await foreach (var line in _queue.Reader.ReadAllAsync())
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                if (bytes.Length > _maxBytes)
                {
                    bytes = bytes.AsSpan(0, (int)_maxBytes).ToArray();
                }

                if (stream.Length > 0 && stream.Length + bytes.Length > _maxBytes)
                {
                    await stream.DisposeAsync();
                    File.Move(_path, _rolledPath, overwrite: true);
                    stream = Open();
                }

                await stream.WriteAsync(bytes);
                if (!_queue.Reader.TryPeek(out _))
                {
                    await stream.FlushAsync();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync($"ai-proxy: log file {_path} stopped: {exception.Message}");
            _queue.Writer.TryComplete();
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private FileStream Open()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite | FileShare.Delete
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnly;
        }

        var stream = new FileStream(_path, options);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, OwnerOnly);
        }

        return stream;
    }

    private sealed class FileLogger(string category, ChannelWriter<string> queue) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = new StringBuilder()
                .Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
                .Append(' ').Append(logLevel)
                .Append(' ').Append(eventId.Id.ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(category)
                .Append(' ').Append(formatter(state, exception))
                .Append('\n');
            if (exception is not null)
            {
                line.Append(exception).Append('\n');
            }

            queue.TryWrite(line.ToString());
        }
    }
}
