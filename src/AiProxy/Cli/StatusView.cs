using System.Globalization;
using System.Text.Json;
using AiProxy.Hosting.Proxy;

namespace AiProxy.Cli;

/// <summary>What <c>ai-proxy status</c> shows about a running owner; carries no nonce or start token.</summary>
internal sealed record StatusView(int Pid, DateTime StartUtc, string ProxyUrl, string[] ApiUrls, string DataDirectory, string LogFile, ProxyControl.CaptureInfo? Capture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public bool Running => true;

    public static StatusView From(ProxyControl.ProxyInfo owner, string dataDirectory) => new(owner.Pid, owner.StartUtc, owner.ProxyUrl, owner.ApiUrls, dataDirectory, Path.Combine(dataDirectory, ProxyLogFile.FileName), owner.Capture);

    public string Json() => JsonSerializer.Serialize(this, JsonOptions);

    public string Text(DateTimeOffset now)
    {
        var uptime = now - new DateTimeOffset(StartUtc);
        return string.Join(Environment.NewLine,
            ["ai-proxy is running",
            $"  pid:     {Pid.ToString(CultureInfo.InvariantCulture)}",
            $"  started: {StartUtc:u} (up {(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s)",
            $"  proxy:   {ProxyUrl}",
            $"  api:     {string.Join(", ", ApiUrls)}",
            $"  data:    {DataDirectory}",
            .. CaptureLines(),
            $"  log:     {LogFile}"]);
    }

    private IEnumerable<string> CaptureLines()
    {
        switch (Capture)
        {
            case null:
                return ["  capture: unknown (the running proxy does not report it)"];
            case { State: { } state }:
                return
                [
                    $"  capture: on, {CaptureText.Size(state)}",
                    $"    oldest row:       {CaptureText.Time(state.OldestRowAt)}",
                    $"    last eviction:    {CaptureText.Time(state.LastEvictionAt)}",
                    $"    last write error: {state.LastWriteError ?? "none"}{(state.LastWriteErrorAt is null ? "" : $" (at {CaptureText.Time(state.LastWriteErrorAt)})")}"
                ];
            default:
                return [Capture.Enabled ? "  capture: on" : "  capture: off"];
        }
    }
}
