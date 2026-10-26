using AiProxy.Client;
using AiProxy.Mcp;

namespace AiProxy.Tests.Unit.Mcp;

/// <summary>The MCP server reaches the proxy only through its HTTP client, never through the proxy's storage.</summary>
public sealed class McpDependencyTests
{
    [Fact]
    public void The_server_and_http_client_live_in_the_proxy_assembly()
    {
        var assembly = typeof(RetrievalTools).Assembly;
        var references = assembly.GetReferencedAssemblies().Select(static reference => reference.Name).ToArray();

        Assert.Equal(typeof(ProxyApplication).Assembly, assembly);
        Assert.True(typeof(IProxyClient).IsAssignableFrom(typeof(ProxyClient)));
        Assert.DoesNotContain("AiProxy.Client", references);
    }
}