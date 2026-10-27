using System.Net;
using AiProxy.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Tests.Integration;

public sealed class ProxyPortTests
{
    [Fact]
    public void An_unset_port_keeps_the_default_8080()
    {
        var (address, options) = Compose(null);

        Assert.Equal(ProxyRuntimeOptions.DefaultPort, address.Port);
        Assert.Equal(ProxyRuntimeOptions.DefaultPort, options.Port);
    }

    [Fact]
    public void An_empty_port_is_unset_and_keeps_the_default_8080()
    {
        var (_, options) = Compose("");

        Assert.Equal(ProxyRuntimeOptions.DefaultPort, options.Port);
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
    [InlineData("70000")]
    [InlineData("-1")]
    [InlineData("  ")]
    public void An_invalid_port_fails_startup_naming_the_setting(string value)
    {
        var failure = Assert.ThrowsAny<Exception>(() => Compose(value));

        Assert.Contains("Proxy:Port", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Isolated_configuration_controls_runtime_and_port_zero_publishes_the_bound_port()
    {
        using var directory = new TemporaryDirectory();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Proxy:Port"] = "0";
        builder.Configuration["Proxy:DataDirectory"] = directory.Path;
        builder.Configuration["Proxy:ConfigureSystemProxy"] = "false";
        builder.Configuration["Proxy:TrustRootCertificate"] = "false";
        builder.Configuration["Proxy:Compression:Enabled"] = "false";
        await using var app = ProxyApplication.Create(builder);
        var options = app.Services.GetRequiredService<ProxyRuntimeOptions>();
        Assert.Equal(directory.Path, options.DataDirectory);
        Assert.False(options.ConfigureSystemProxy);
        Assert.False(options.TrustRootCertificate);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<TitaniumProxyService>().Address;
        Assert.InRange(address.Port, 1, 65535);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Absent_runtime_settings_preserve_injected_options(bool enabled)
    {
        using var directory = new TemporaryDirectory();
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Proxy:Compression:Enabled"] = "false";
        var injected = new ProxyRuntimeOptions(IPAddress.Loopback, 0, enabled, enabled, directory.Path);
        await using var app = ProxyApplication.Create(builder, runtimeOptions: injected);
        Assert.Equal(injected, app.Services.GetRequiredService<ProxyRuntimeOptions>());
        Assert.False(Directory.Exists(directory.Path));
    }

    private static (Uri Address, ProxyRuntimeOptions Options) Compose(string? port)
    {
        var builder = WebApplication.CreateBuilder();
        if (port is not null)
        {
            builder.Configuration["Proxy:Port"] = port;
        }

        var app = ProxyApplication.Create(builder);
        return (app.Services.GetRequiredService<TitaniumProxyService>().Address, app.Services.GetRequiredService<ProxyRuntimeOptions>());
    }
}
