using AiProxy.Cli;
using AiProxy.Client;

namespace AiProxy.Tests.Unit.Cli;

/// <summary>The CLI reaches the proxy only through its HTTP client, never through the proxy's storage.</summary>
public sealed class CliDependencyTests
{
    [Fact]
    public void The_cli_and_http_client_live_in_the_proxy_assembly()
    {
        var assembly = typeof(GainCommand).Assembly;
        var references = assembly.GetReferencedAssemblies().Select(static reference => reference.Name).ToArray();

        Assert.Equal(typeof(ProxyApplication).Assembly, assembly);
        Assert.True(typeof(IProxyClient).IsAssignableFrom(typeof(ProxyClient)));
        Assert.DoesNotContain("AiProxy.Client", references);
    }
}