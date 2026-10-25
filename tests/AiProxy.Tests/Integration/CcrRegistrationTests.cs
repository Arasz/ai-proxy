using AiProxy.Core.Content.Ccr;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Tests.Integration;

public sealed class CcrRegistrationTests
{
    [Fact]
    public async Task Compression_registers_separate_transient_preview_and_retrieval_components_without_activating_ccr()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["PROXY_COMPRESSION"] = "on";
        builder.Configuration["PROXY_CAPTURE"] = "off";
        builder.Configuration["PROXY_PORT"] = "8080";
        await using var app = ProxyApplication.Create(builder);

        foreach (var name in new[] { "CcrPreviewBuilder", "CcrPreviewOverlay", "CcrRetriever", "CcrRetrievalParser" })
        {
            var descriptor = Assert.Single(builder.Services, registration => registration.ServiceType.Name == name);
            Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
            Assert.NotSame(app.Services.GetRequiredService(descriptor.ServiceType), app.Services.GetRequiredService(descriptor.ServiceType));
        }

        Assert.Null(app.Services.GetService<CcrOptions>());
    }
}
