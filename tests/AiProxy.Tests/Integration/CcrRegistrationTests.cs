using AiProxy.Ccr;
using AiProxy.Core.Content.Ccr;
using AiProxy.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Tests.Integration;

public sealed class CcrRegistrationTests
{
    [Fact]
    public async Task Compression_registers_separate_transient_preview_and_retrieval_components_and_the_ccr_runtime()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Proxy:Compression:Enabled"] = "true";
        builder.Configuration["Proxy:Capture"] = "false";
        builder.Configuration["Proxy:Port"] = ProxyRuntimeOptions.DefaultPort.ToString();
        await using var app = ProxyApplication.Create(builder);

        foreach (var name in new[] { "CcrPreviewBuilder", "CcrPreviewOverlay", "CcrRetriever", "CcrRetrievalParser" })
        {
            var descriptor = Assert.Single(builder.Services, registration => registration.ServiceType.Name == name);
            Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
            Assert.NotSame(app.Services.GetRequiredService(descriptor.ServiceType), app.Services.GetRequiredService(descriptor.ServiceType));
        }

        Assert.Same(CcrOptions.Default, app.Services.GetService<CcrOptions>());
        Assert.IsType<CcrRuntime>(app.Services.GetRequiredService<ICcrRuntime>());
    }
}
