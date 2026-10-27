using System.Collections.Frozen;
using AiProxy.Core.Gain;

namespace AiProxy;

internal static class KnownNames
{
    internal static class Configuration
    {
        public const string Urls = "urls";
        public const string HttpPorts = "HTTP_PORTS";
        public const string HttpsPorts = "HTTPS_PORTS";
        public const string KestrelEndpoints = "Kestrel:Endpoints";
        public const string AllowedHosts = "AllowedHosts";
        public const string ProxyPort = "Proxy:Port";
    }

    internal static class EnvironmentVariables
    {
        public const string HttpProxy = "HTTP_PROXY";
        public const string HttpsProxy = "HTTPS_PROXY";
        public const string LowerHttpProxy = "http_proxy";
        public const string LowerHttpsProxy = "https_proxy";
        public const string NoProxy = "NO_PROXY";
        public const string LowerNoProxy = "no_proxy";
        public const string AnthropicCustomHeaders = "ANTHROPIC_CUSTOM_HEADERS";
        public const string NodeExtraCaCerts = "NODE_EXTRA_CA_CERTS";
    }

    internal static class Network
    {
        public const string RootPath = "/";
        public const string ProxyUrlFormat = "http://127.0.0.1:{0}";
        public const string LoopbackHosts = "localhost;127.0.0.1;[::1]";
        public const string LoopbackBypass = "localhost,127.0.0.1,::1";
        public static readonly FrozenSet<string> Protocols = ["http", "https"];
    }

    internal static class Headers
    {
        public const string ProjectPrefix = $"{GainApi.ProjectHeader}: ";
    }

    internal static class MarkerDiscovery
    {
        public const string MacLsof = "/usr/sbin/lsof";
        public const string LinuxLsof = "/usr/bin/lsof";
        public const string ProcessIds = "-t";
        public const string EndOptions = "--";
        public const string RestartManager = "rstrtmgr.dll";
        public const string UnknownOwner = "the process holding the system proxy marker";
        public const string KnownOwnerFormat = "the process holding the system proxy marker (PID {0})";
        public const string StopOwnerFormat = "Stop {0} manually, then start ai-proxy again.";
    }
}
