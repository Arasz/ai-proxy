namespace AiProxy.Hosting.Api;

/// <summary>Logs the request that came in and the status that went out.</summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    /// <summary>Logs the request and the status the caller got.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        Log.Requested(logger, context.Request.Method, context.Request.Path.Value ?? "/");
        await next(context);
        Log.Answered(logger, context.Request.Method, context.Request.Path.Value ?? "/", context.Response.StatusCode);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "{Method} {Path}")]
        public static partial void Requested(ILogger logger, string method, string path);

        [LoggerMessage(EventId = 1202, Level = LogLevel.Information, Message = "{Method} {Path} answered {Status}")]
        public static partial void Answered(ILogger logger, string method, string path, int status);
    }
}
