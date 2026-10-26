namespace AiProxy.Core.Gain;

/// <summary>What the proxy and its clients must agree on about the gain and retrieval API.</summary>
public static class GainApi
{
    /// <summary>The loopback URL the proxy serves the API on unless told otherwise, and the clients' default.</summary>
    public const string DefaultEndpoint = "http://127.0.0.1:17432";

    /// <summary>The request header that names a request's project; the proxy removes it before forwarding.</summary>
    public const string ProjectHeader = "X-Ai-Proxy-Project";
}