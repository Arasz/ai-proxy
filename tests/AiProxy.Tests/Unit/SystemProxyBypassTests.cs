using AiProxy.Hosting.Integration;
using Titanium.Web.Proxy;

namespace AiProxy.Tests.Unit;

public sealed class SystemProxyBypassTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Loopback_hosts_bypass_the_system_proxy(string host)
    {
        var settings = TitaniumSystemProxyRegistrar.BypassSettings();

        Assert.Contains(host, settings.BypassRules);
    }

    [Fact]
    public void The_bypass_rules_are_added_to_the_ones_already_set()
    {
        Assert.Equal(SystemProxyBypassRuleMode.Merge, TitaniumSystemProxyRegistrar.BypassSettings().BypassRuleMode);
    }
}
