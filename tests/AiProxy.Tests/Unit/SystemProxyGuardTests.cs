using AiProxy.Hosting;
using AiProxy.Hosting.Guard;

namespace AiProxy.Tests.Unit;

public sealed class SystemProxyGuardTests
{
    [Fact]
    public async Task Input_that_ends_without_restored_clears_the_system_proxy()
    {
        var clears = 0;

        var cleared = await new SystemProxyGuard(() =>
        {
            try
            {
                clears++;
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }).Run(new StringReader(""));

        Assert.True(cleared);
        Assert.Equal(1, clears);
    }

    [Fact]
    public async Task Input_that_reports_restored_leaves_the_system_proxy_alone()
    {
        var clears = 0;

        var cleared = await new SystemProxyGuard(() =>
        {
            try
            {
                clears++;
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }).Run(new StringReader("restored\n"));

        Assert.False(cleared);
        Assert.Equal(0, clears);
    }

    [Theory]
    [InlineData("garbage\n")]
    [InlineData("restor")]
    [InlineData("Restored extra\n")]
    [InlineData("restoredX\n")]
    public async Task Input_that_is_not_exactly_restored_clears_the_system_proxy(string input)
    {
        var clears = 0;

        await new SystemProxyGuard(() =>
        {
            try
            {
                clears++;
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }).Run(new StringReader(input));

        Assert.Equal(1, clears);
    }
}
