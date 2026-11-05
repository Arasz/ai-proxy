using System.Text;

namespace AiProxy.Core.Gain;

/// <summary>Reads a request's project from a proxy credential and picks between project sources.</summary>
public static class ProjectCredential
{
    private const string BasicScheme = "Basic ";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The project in a Basic <c>Proxy-Authorization</c> value, or null when it is not an ai-proxy credential.</summary>
    public static string? Read(string? proxyAuthorization)
    {
        if (proxyAuthorization is null || !proxyAuthorization.StartsWith(BasicScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var encoded = proxyAuthorization.AsSpan(BasicScheme.Length).Trim();
        var buffer = new byte[encoded.Length];
        if (!Convert.TryFromBase64Chars(encoded, buffer, out var written))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(buffer, 0, written);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0 || decoded[..separator] != GainApi.ProxyUser)
        {
            return null;
        }

        var project = decoded[(separator + 1)..];
        return project.Length == 0 || project.AsSpan().IndexOfAny('\r', '\n') >= 0 ? null : project;
    }

    /// <summary>The proxy URL for an endpoint with the project as the percent-encoded password of <see cref="GainApi.ProxyUser" />.</summary>
    public static string ProxyUrl(Uri endpoint, string project)
    {
        return $"{endpoint.Scheme}://{GainApi.ProxyUser}:{Uri.EscapeDataString(project)}@{endpoint.Authority}";
    }

    /// <summary>Picks the project: the header, else the request credential, else the tunnel's credential.</summary>
    public static string? Resolve(string? header, string? requestCredential, string? connectCredential)
    {
        return !string.IsNullOrEmpty(header) ? header : requestCredential ?? connectCredential;
    }
}
