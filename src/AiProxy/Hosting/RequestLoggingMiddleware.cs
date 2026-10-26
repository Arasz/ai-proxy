namespace AiProxy.Hosting;

/// <summary>Logs the request that came in and the status that went out.</summary>
public sealed partial class RequestLoggingMiddleware
{
    private readonly ILogger<RequestLoggingMiddleware> _logger;
    private readonly RequestDelegate _next;

    /// <summary>Holds the next middleware and the logger.</summary>
    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>Logs the request, and the status the caller got.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Log.Requested(_logger, context.Request.Method, context.Request.Path.Value ?? "/");
        await _next(context);
        Log.Answered(_logger, context.Request.Method, context.Request.Path.Value ?? "/", context.Response.StatusCode);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "{Method} {Path}")]
        public static partial void Requested(ILogger logger, string method, string path);

        [LoggerMessage(EventId = 1202, Level = LogLevel.Information, Message = "{Method} {Path} answered {Status}")]
        public static partial void Answered(ILogger logger, string method, string path, int status);
    }
}