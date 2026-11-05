using System.Text;
using AiProxy.Core.Gain;

namespace AiProxy.Tests.Unit.Gain;

/// <summary>Reading the project out of a proxy credential and choosing between project sources.</summary>
public sealed class ProjectCredentialTests
{
    private static string Basic(string user, string password) => Basic(Encoding.UTF8.GetBytes($"{user}:{password}"));

    private static string Basic(byte[] bytes) => "Basic " + Convert.ToBase64String(bytes);

    [Fact]
    public void Reads_path()
    {
        Assert.Equal("/project/a", ProjectCredential.Read(Basic("ai-proxy", "/project/a")));
    }

    [Fact]
    public void Splits_at_first_colon()
    {
        Assert.Equal(@"C:\U:x", ProjectCredential.Read(Basic("ai-proxy", @"C:\U:x")));
    }

    [Fact]
    public void Decodes_utf8()
    {
        Assert.Equal("Użytk/zß", ProjectCredential.Read(Basic("ai-proxy", "Użytk/zß")));
    }

    [Fact]
    public void Ignores_another_user()
    {
        Assert.Null(ProjectCredential.Read(Basic("someone", "/project/a")));
    }

    [Theory]
    [InlineData("Bearer abc")]
    [InlineData("Digest abc")]
    public void Ignores_non_basic(string scheme)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("ai-proxy:/project/a"));

        Assert.Null(ProjectCredential.Read($"{scheme.Split(' ')[0]} {payload}"));
    }

    [Fact]
    public void Accepts_scheme_in_any_case()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("ai-proxy:/p"));

        Assert.Equal("/p", ProjectCredential.Read("bAsIc " + payload));
    }

    [Fact]
    public void Ignores_empty_password()
    {
        Assert.Null(ProjectCredential.Read(Basic("ai-proxy", "")));
    }

    [Theory]
    [InlineData("/a\nb")]
    [InlineData("/a\rb")]
    public void Rejects_newline(string path)
    {
        Assert.Null(ProjectCredential.Read(Basic("ai-proxy", path)));
    }

    [Fact]
    public void Ignores_malformed_base64()
    {
        Assert.Null(ProjectCredential.Read("Basic !!!not-base64!!!"));
    }

    [Fact]
    public void Ignores_invalid_utf8()
    {
        Assert.Null(ProjectCredential.Read(Basic([.. "ai-proxy:"u8, 0xFF, 0xFE])));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic")]
    [InlineData("Basic ")]
    public void Ignores_absent_or_incomplete(string? value)
    {
        Assert.Null(ProjectCredential.Read(value));
    }

    [Fact]
    public void Header_wins_over_request_credential()
    {
        Assert.Equal("/header", ProjectCredential.Resolve("/header", "/request", "/connect"));
    }

    [Fact]
    public void Request_credential_wins_over_connect_credential()
    {
        Assert.Equal("/request", ProjectCredential.Resolve(null, "/request", "/connect"));
    }

    [Fact]
    public void Connect_credential_is_the_last_source()
    {
        Assert.Equal("/connect", ProjectCredential.Resolve(null, null, "/connect"));
    }

    [Fact]
    public void Empty_header_does_not_count()
    {
        Assert.Equal("/request", ProjectCredential.Resolve("", "/request", null));
    }

    [Fact]
    public void Nothing_resolves_to_null()
    {
        Assert.Null(ProjectCredential.Resolve(null, null, null));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080", @"/home/me/app", "http://ai-proxy:%2Fhome%2Fme%2Fapp@127.0.0.1:8080")]
    [InlineData("http://[::1]:8080", @"/home/me/app", "http://ai-proxy:%2Fhome%2Fme%2Fapp@[::1]:8080")]
    [InlineData("http://127.0.0.1:8080", @"C:\Users\x", "http://ai-proxy:C%3A%5CUsers%5Cx@127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080", "a b/ż", "http://ai-proxy:a%20b%2F%C5%BC@127.0.0.1:8080")]
    public void Proxy_url_carries_the_escaped_project(string endpoint, string project, string expected)
    {
        Assert.Equal(expected, ProjectCredential.ProxyUrl(new Uri(endpoint), project));
    }

    [Theory]
    [InlineData("http://[::1]:8080", @"C:\Users\x")]
    [InlineData("http://127.0.0.1:8080", "a b/ż%41:@?#")]
    public void Proxy_url_userinfo_decodes_to_the_project(string endpoint, string project)
    {
        var url = new Uri(ProjectCredential.ProxyUrl(new Uri(endpoint), project));
        var userInfo = Uri.UnescapeDataString(url.UserInfo);

        Assert.Equal($"{GainApi.ProxyUser}:{project}", userInfo);
        Assert.Equal(new Uri(endpoint).Authority, url.Authority);
    }
}
