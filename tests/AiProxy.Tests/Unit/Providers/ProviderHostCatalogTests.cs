using AiProxy.Core.Providers;
using AiProxy.Hosting;

namespace AiProxy.Tests.Unit.Providers;

public sealed class ProviderHostCatalogTests
{
    [Theory]
    [InlineData("api.openai.com", Dialect.OpenAi)]
    [InlineData("api.anthropic.com", Dialect.Anthropic)]
    [InlineData("generativelanguage.googleapis.com", Dialect.Gemini)]
    [InlineData("openrouter.ai", Dialect.OpenAi)]
    [InlineData("chatgpt.com", Dialect.OpenAi)]
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
    [InlineData("www.chatgpt.com")]
    public void An_unrecognized_host_is_left_for_default_pass_through(string host)
    {
        Assert.Null(ProviderHostCatalog.Find(host));
    }

    [Fact]
    public void Chatgpt_com_is_the_only_harness_only_host_in_the_catalog()
    {
        Assert.Equal(["chatgpt.com"], ProviderHostCatalog.Hosts.Where(ProviderHostCatalog.IsHarnessOnly));
    }

    [Theory]
    [InlineData("chatgpt.com.")]
    [InlineData("ChatGPT.com")]
    public void A_harness_only_host_is_matched_like_any_provider_host(string host)
    {
        Assert.True(ProviderHostCatalog.IsHarnessOnly(host));
    }

    [Theory]
    [InlineData("api.openai.com")]
    [InlineData("www.chatgpt.com")]
    [InlineData("example.test")]
    public void Other_hosts_are_not_harness_only(string host)
    {
        Assert.False(ProviderHostCatalog.IsHarnessOnly(host));
    }

    [Theory]
    [InlineData("chatgpt.com", Dialect.OpenAi, true)]
    [InlineData("api.openai.com", Dialect.OpenAi, false)]
    [InlineData("api.anthropic.com", Dialect.Anthropic, false)]
    public void The_canonical_hosts_give_the_dialect_and_the_harness_only_flag_together(string host, Dialect dialect, bool harnessOnly)
    {
        Assert.Equal(new ProviderHost(dialect, harnessOnly), new CanonicalProviderHosts().Find(host));
    }
}
