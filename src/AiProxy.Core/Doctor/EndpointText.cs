namespace AiProxy.Core.Doctor;

/// <summary>Renders a proxy address for output without the credentials, path or query it may carry.</summary>
public static class EndpointText
{
    private const string Unparseable = "(unparseable)";

    /// <summary>Returns <c>scheme://host:port</c>, assuming http when the value has no scheme.</summary>
    public static string Safe(string value)
    {
        var text = value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? $"{uri.Scheme}://{uri.Host}:{uri.Port}"
            : Unparseable;
    }
}
