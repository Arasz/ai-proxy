using AiProxy.Core.Doctor;

namespace AiProxy.Doctor;

/// <summary>Compares the proxy variables of this shell with the running proxy's address.</summary>
internal sealed class EnvironmentCheck(Uri proxy, Func<string, string?> variable)
{
    public const string Name = "environment";

    private static readonly string[] Variables =
    [
        KnownNames.EnvironmentVariables.HttpsProxy, KnownNames.EnvironmentVariables.LowerHttpsProxy,
        KnownNames.EnvironmentVariables.HttpProxy, KnownNames.EnvironmentVariables.LowerHttpProxy
    ];

    public Task<CheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var expected = EndpointText.Safe(proxy.ToString());
        var set = Variables
            .Select(name => (Name: name, Value: variable(name)))
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => new Setting(pair.Name, EndpointText.Safe(pair.Value!)))
            .DistinctBy(static setting => (setting.Name.ToUpperInvariant(), setting.Shown))
            .ToList();

        CheckResult result;
        if (set.Count == 0)
        {
            result = new CheckResult(Name, CheckStatus.Pass, "not set; clients using the system proxy are covered.");
        }
        else if (set.Find(setting => !Matches(setting.Shown)) is { } other)
        {
            result = new CheckResult(Name, CheckStatus.Fail, $"{other.Name} points at {other.Shown}, not at the proxy {expected}.", $"Unset {other.Name}, or point it at {expected}.");
        }
        else
        {
            result = new CheckResult(Name, CheckStatus.Pass, string.Join(", ", set.Select(static setting => $"{setting.Name}={setting.Shown}")) + " matches the proxy.");
        }

        return Task.FromResult(result);
    }

    private bool Matches(string shown)
    {
        return Uri.TryCreate(shown, UriKind.Absolute, out var uri) && uri.Port == proxy.Port && SameHost(uri.Host, proxy.Host);
    }

    private static bool SameHost(string left, string right)
    {
        return Canonical(left).Equals(Canonical(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Canonical(string host)
    {
        return host == "localhost" ? "127.0.0.1" : host;
    }

    private sealed record Setting(string Name, string Shown);
}
