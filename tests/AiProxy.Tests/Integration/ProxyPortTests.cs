using AiProxy.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Tests.Integration;

public sealed class ProxyPortTests
{
    [Fact]
    public void An_unset_port_keeps_the_default_8080()
    {
        var (address, options) = Compose(null);

        Assert.Equal(8080, address.Port);
        Assert.Equal(8080, options.Port);
    }

    [Fact]
    public void An_empty_port_is_unset_and_keeps_the_default_8080()
    {
        var (_, options) = Compose("");

        Assert.Equal(8080, options.Port);
    }

    [Fact]
    public void A_valid_port_is_used_for_the_listener_address()
    {
        var (address, options) = Compose("18123");

        Assert.Equal(18123, address.Port);
        Assert.Equal(18123, options.Port);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    [InlineData("  ")]
    public void An_invalid_port_fails_startup_naming_the_setting(string value)
    {
        var failure = Assert.ThrowsAny<Exception>(() => Compose(value));

        Assert.Contains("Proxy:Port", failure.Message, StringComparison.Ordinal);
    }

    private static (Uri Address, ProxyRuntimeOptions Options) Compose(string? port)
    {
        var builder = WebApplication.CreateBuilder();
        if (port is not null) builder.Configuration["Proxy:Port"] = port;

        var app = ProxyApplication.Create(builder);
        return (app.Services.GetRequiredService<TitaniumProxyService>().Address, app.Services.GetRequiredService<ProxyRuntimeOptions>());
    }
}