using Microsoft.Extensions.Logging;

namespace AiProxy.Hosting.Integration;

/// <summary>The logger factory Titanium writes to: a failed certificate store step becomes a one-line warning, every other entry passes through.</summary>
/// <param name="inner">The host's logger factory, which stays owned by the host.</param>
internal sealed partial class TitaniumLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return new TitaniumLogger(inner.CreateLogger(categoryName));
    }

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        inner.AddProvider(provider);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class TitaniumLogger(ILogger logger) : ILogger
    {
        private const string CertificateFailure = "Certificate operation failed";

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return logger.BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logger.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && exception is not null && formatter(state, exception) == CertificateFailure)
            {
                TitaniumLoggerFactory.Log.CertificateStoreStepFailed(logger, Describe(exception));
                return;
            }

            logger.Log(logLevel, eventId, state, exception, formatter);
        }

        private static string Describe(Exception exception)
        {
            return exception.InnerException is { } cause ? $"{exception.Message}: {cause.Message}" : exception.Message;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1310, Level = LogLevel.Warning, Message = "A certificate store step failed; on macOS the system tools complete the trust change: {Failure}")]
        public static partial void CertificateStoreStepFailed(ILogger logger, string failure);
    }
}
