using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AiProxy.Tests.Integration;

/// <summary>One log call as a test sees it.</summary>
internal sealed record LogEntry(string Category, LogLevel Level, int EventId, Exception? Exception);

/// <summary>Keeps every log call of every logger it creates, so a test can assert on event ids.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, eventId.Id, exception));
    }
}
