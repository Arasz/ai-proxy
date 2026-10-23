using AiProxy.Hosting;

namespace AiProxy.Tests.Unit;

public class SystemProxyGuardTests
{
    [Fact]
    public void Input_that_ends_without_restored_clears_the_system_proxy()
    {
        var clears = 0;

        var cleared = new SystemProxyGuard(() => clears++).Run(new StringReader(string.Empty));

        Assert.True(cleared);
        Assert.Equal(1, clears);
    }

    [Fact]
    public void Input_that_reports_restored_leaves_the_system_proxy_alone()
    {
        var clears = 0;

        var cleared = new SystemProxyGuard(() => clears++).Run(new StringReader("restored\n"));

        Assert.False(cleared);
        Assert.Equal(0, clears);
    }

    [Theory]
    [InlineData("garbage\n")]
    [InlineData("restor")]
    [InlineData("Restored extra\n")]
    [InlineData("restoredX\n")]
    public void Input_that_is_not_exactly_restored_clears_the_system_proxy(string input)
    {
        var clears = 0;

        new SystemProxyGuard(() => clears++).Run(new StringReader(input));

        Assert.Equal(1, clears);
    }
}
