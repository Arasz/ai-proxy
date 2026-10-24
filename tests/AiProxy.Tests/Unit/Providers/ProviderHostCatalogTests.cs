using AiProxy.Core.Providers;

namespace AiProxy.Tests.Unit.Providers;

public sealed class ProviderHostCatalogTests
{
    [Theory]
    [InlineData("api.openai.com", Dialect.OpenAi)]
    [InlineData("api.anthropic.com", Dialect.Anthropic)]
    [InlineData("generativelanguage.googleapis.com", Dialect.Gemini)]
    [InlineData("openrouter.ai", Dialect.OpenAi)]
    public void A_known_provider_host_selects_its_request_dialect(string host, Dialect dialect)
    {
        Assert.Equal(dialect, ProviderHostCatalog.Find(host));
    }

    [Theory]
    [InlineData("us.api.openai.com")]
    [InlineData("eu.api.openai.com")]
    [InlineData("gb.api.openai.com")]
    [InlineData("ca.api.openai.com")]
    [InlineData("au.api.openai.com")]
    [InlineData("jp.api.openai.com")]
    [InlineData("kr.api.openai.com")]
    [InlineData("sg.api.openai.com")]
    [InlineData("in.api.openai.com")]
    [InlineData("ae.api.openai.com")]
    [InlineData("eu.openrouter.ai")]
    public void A_documented_regional_api_host_selects_the_openai_dialect(string host)
    {
        Assert.Equal(Dialect.OpenAi, ProviderHostCatalog.Find(host));
    }

    [Theory]
    [InlineData("api.openai.com.attacker.test")]
    [InlineData("openai.com")]
    [InlineData("mcp.example.test")]
    [InlineData("mcp.openrouter.ai")]
    [InlineData("xx.api.openai.com")]
    [InlineData("eu.api.anthropic.com")]
    public void An_unrecognized_host_is_left_for_default_pass_through(string host)
    {
        Assert.Null(ProviderHostCatalog.Find(host));
    }

    [Theory]
    [InlineData("api.openai.com", true)]
    [InlineData("api.anthropic.com", true)]
    [InlineData("generativelanguage.googleapis.com", true)]
    [InlineData("openrouter.ai", true)]
    [InlineData("mcp.example.test", false)]
    [InlineData("api.openai.com.attacker.test", false)]
    public void Https_is_decrypted_only_for_a_canonical_provider_host(string host, bool expected)
    {
        Assert.Equal(expected, ProviderHostCatalog.ShouldInspectHttps(host));
    }

    [Theory]
    [InlineData("api.openai.com:443", true)]
    [InlineData("api.anthropic.com:8443", true)]
    [InlineData("eu.api.openai.com:443", true)]
    [InlineData("eu.openrouter.ai:443", true)]
    [InlineData("mcp.openrouter.ai:443", false)]
    [InlineData("mcp.example.test:443", false)]
    [InlineData("api.openai.com.attacker.test:443", false)]
    public void A_connect_authority_decrypts_only_canonical_provider_hosts(string authority, bool expected)
    {
        Assert.Equal(expected, ProviderHostCatalog.ShouldInspectHttpsAuthority(authority));
    }
}
