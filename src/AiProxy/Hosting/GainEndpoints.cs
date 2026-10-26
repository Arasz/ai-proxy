using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiProxy.Core.Gain;
using AiProxy.Core.Content.Ccr;

namespace AiProxy.Hosting;

/// <summary>Maps the proxy-owned retrieval and statistics API.</summary>
public static partial class GainEndpoints
{
    private static readonly JsonSerializerOptions RetrievalJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void MapGainEndpoints(this WebApplication app)
    {
        app.MapGet("/gain", async (HttpRequest request, IGainStore store, CancellationToken cancellationToken) =>
        {
            if (!TryQuery(request.Query, out var query, out var error))
            {
                return Results.Problem(title: "invalid_gain_filter", detail: error, statusCode: 400);
            }
            return Results.Json(await store.QueryAsync(query, cancellationToken));
        });
        app.MapPost("/gain/reset", async (GainQuery query, IGainStore store, CancellationToken cancellationToken) =>
        {
            var error = Validate(query);
            return error is not null
                ? Results.Problem(title: "invalid_gain_filter", detail: error, statusCode: 400)
                : Results.Json(await store.ResetAsync(query, cancellationToken));
        });
        app.MapPost("/retrieval", async (RetrievalRequest request, HttpContext context, IGainStore gain, TimeProvider time, CancellationToken cancellationToken) =>
        {
            var store = context.RequestServices.GetService<IOriginalStore>();
            var retriever = context.RequestServices.GetService<CcrRetriever>();
            if (store is null || retriever is null)
            {
                return Results.Problem(title: "retrieval_disabled", detail: "Enable proxy compression to retain and retrieve originals.", statusCode: 503);
            }
            var arguments = JsonSerializer.Serialize(request, RetrievalJson);
            var parser = context.RequestServices.GetRequiredService<CcrRetrievalParser>();
            var options = context.RequestServices.GetRequiredService<CcrOptions>();
            var parsed = parser.Parse(arguments, options);
            var original = parsed.Request is { } valid ? await store.FindAsync(valid.Id, cancellationToken) : null;
            var answer = retriever.Answer(arguments, original, options);
            var project = context.Request.Headers[GainApi.ProjectHeader].FirstOrDefault();
            try
            {
                await gain.RecallAsync(new GainRecall(time.GetUtcNow(), request.Id ?? "", project, project is null ? "unknown" : "caller",
                    request.Mode ?? "", answer.Text.Length, GainReportBuilder.Tokens(answer.Text.Length), answer.Error),
                    null, "retrieve_original", "unknown", "retrieval", "ccr", cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Log.RecallWriteFailed(context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GainEndpoints)), exception);
            }

            return Results.Json(new RetrievalResponse(answer.Text, answer.Error));
        });
    }

    private static bool TryQuery(IQueryCollection values, out GainQuery query, out string? error)
    {
        query = new GainQuery();
        error = null;
        string? Value(string key) => values.TryGetValue(key, out var value) ? value.ToString() : null;
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        if (Value("from") is { } fromValue)
        {
            if (!DateTimeOffset.TryParse(fromValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                error = "from must be an ISO date or timestamp.";
                return false;
            }
            from = parsed;
        }
        if (Value("to") is { } toValue)
        {
            if (!DateTimeOffset.TryParse(toValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                error = "to must be an ISO date or timestamp.";
                return false;
            }
            to = parsed;
        }
        var limit = 50;
        if (Value("historyLimit") is { } limitValue && !int.TryParse(limitValue, NumberStyles.None, CultureInfo.InvariantCulture, out limit))
        {
            error = "historyLimit must be an integer from 0 to 1000.";
            return false;
        }
        query = new GainQuery(Value("project"), from, to, Value("provider"), Value("method"), Value("rule"), Value("layer"), Value("pipeline"), limit);
        error = Validate(query);
        return error is null;
    }

    private static string? Validate(GainQuery query)
    {
        if (query.HistoryLimit is < 0 or > 1000) { return "historyLimit must be from 0 to 1000."; }
        if (query.From is { } from && query.To is { } to && from >= to) { return "from must be earlier than to."; }
        foreach (var value in new[] { query.Project, query.Provider, query.Method, query.Rule, query.Layer, query.Pipeline })
        {
            if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)))
            {
                return "Filters must be nonblank text without control characters, at most 4096 characters.";
            }
        }
        return null;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1410, Level = LogLevel.Warning, Message = "A retrieval was answered but its recall could not be recorded")]
        public static partial void RecallWriteFailed(ILogger logger, Exception exception);
    }
}
