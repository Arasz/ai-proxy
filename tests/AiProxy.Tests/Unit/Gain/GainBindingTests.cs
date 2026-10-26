using AiProxy.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace AiProxy.Tests.Unit.Gain;

public sealed class GainBindingTests
{
    [Fact]
    public async Task Default_communication_binding_is_unique_and_explicit_bindings_win()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["urls"] = null;
        builder.Configuration["HTTP_PORTS"] = null;
        builder.Configuration["HTTPS_PORTS"] = null;
        builder.Configuration["Proxy:Compression:Enabled"] = "false";
        await using var app = ProxyApplication.Create(builder);
        Assert.Equal("http://127.0.0.1:17432", builder.Configuration["urls"]);
        var explicitBuilder = WebApplication.CreateBuilder();
        explicitBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        explicitBuilder.Configuration["Proxy:Compression:Enabled"] = "false";
        await using var configured = ProxyApplication.Create(explicitBuilder);
        Assert.Equal("http://127.0.0.1:0", explicitBuilder.Configuration["urls"]);
    }
}
