using AiProxy.Client;
using AiProxy.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length is not 0 && (args.Length != 2 || args[0] != "--endpoint"))
{
    Console.Error.WriteLine("Usage: ai-proxy-mcp [--endpoint http://127.0.0.1:17432]");
    return 2;
}

var endpoint = args.Length == 2 ? args[1] : ProxyClientOptions.DefaultEndpoint;
if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine("--endpoint must be an absolute HTTP or HTTPS proxy URL.");
    return 2;
}

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
using var http = ProxyClient.CreateHttpClient(endpoint);
builder.Services.AddSingleton(new ProxyClient(http));
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<RetrievalTools>();
using var host = builder.Build();
await host.RunAsync();
return 0;
