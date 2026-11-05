using System.Globalization;
using System.Text.Json;
using AiProxy.Hosting.Proxy;

namespace AiProxy.Cli;

/// <summary>What <c>ai-proxy status</c> shows about a running owner; carries no nonce or start token.</summary>
internal sealed record StatusView(int Pid, DateTime StartUtc, string ProxyUrl, string[] ApiUrls, string DataDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public bool Running => true;

    public static StatusView From(ProxyControl.ProxyInfo owner, string dataDirectory)
    {
        return new StatusView(owner.Pid, owner.StartUtc, owner.ProxyUrl, owner.ApiUrls, dataDirectory);
    }

    public string Json()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public string Text(DateTimeOffset now)
    {
        var uptime = now - new DateTimeOffset(StartUtc);
        return string.Join(Environment.NewLine,
            "ai-proxy is running",
            $"  pid:     {Pid.ToString(CultureInfo.InvariantCulture)}",
            $"  started: {StartUtc:u} (up {(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s)",
            $"  proxy:   {ProxyUrl}",
            $"  api:     {string.Join(", ", ApiUrls)}",
            $"  data:    {DataDirectory}");
    }
}
