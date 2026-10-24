// Runs the shipped compression rules over each payload as an Anthropic tool result, through the
// same composition the proxy uses, and prints one JSON line per timed run.
// Usage: dotnet run -- <payloads dir> <runs> <payload name>...
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy;
using AiProxy.Core.Compression;
using AiProxy.Core.Providers;

var payloads = args[0];
var runs = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
var store = Directory.CreateTempSubdirectory("reducer-scoring-rules-");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = store.FullName });
builder.Configuration.Sources.Clear();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["PROXY_COMPRESSION"] = "on",
    ["PROXY_COMPRESSION_DB"] = Path.Combine(store.FullName, "content.db"),
});
builder.Logging.ClearProviders();

try
{
    await using var app = ProxyApplication.Create(builder);
    var pipeline = app.Services.GetRequiredService<ICompressionPipeline>();

    foreach (var name in args.Skip(2))
    {
        var text = await File.ReadAllTextAsync(Path.Combine(payloads, name + ".txt"), new UTF8Encoding(false));
        var body = Encoding.UTF8.GetBytes(Request(text));
        await pipeline.CompressAsync(new CompressionRequest(body, "application/json", Dialect.Anthropic), CancellationToken.None);

        for (var run = 0; run < runs; run++)
        {
            var watch = Stopwatch.StartNew();
            var result = await pipeline.CompressAsync(new CompressionRequest(body, "application/json", Dialect.Anthropic), CancellationToken.None);
            watch.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                payload = name,
                run,
                output = ToolResultText(result.Body.Span),
                latency_s = watch.Elapsed.TotalSeconds,
                failures = result.PartFailures.Count,
            }));
        }
    }
}
finally
{
    store.Delete(recursive: true);
}

static string Request(string toolOutput) => new JsonObject
{
    ["model"] = "claude-opus-5-5",
    ["max_tokens"] = 1024,
    ["messages"] = new JsonArray
    {
        new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = "Bash", ["input"] = new JsonObject() },
            },
        },
        new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "toolu_1", ["content"] = toolOutput },
            },
        },
    },
}.ToJsonString();

static string ToolResultText(ReadOnlySpan<byte> body)
{
    var content = JsonNode.Parse(body)!["messages"]![1]!["content"]![0]!["content"]!;
    if (content is JsonValue value)
    {
        return value.GetValue<string>();
    }

    return string.Concat(content.AsArray().Select(block => (string?)block!["text"] ?? string.Empty));
}
