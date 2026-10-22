using AiProxy.Core.Providers;
using AiProxy.Core.Routing;

namespace AiProxy.Tests.Unit.Routing;

/// <summary>The proxy's own credentials, held in memory for the sake of a test.</summary>
internal sealed class FakeCredentialProvider : ICredentialProvider
{
    private readonly Dictionary<Dialect, string> _credentials;

    public FakeCredentialProvider(params (Dialect Dialect, string Credential)[] credentials)
    {
        _credentials = credentials.ToDictionary(pair => pair.Dialect, pair => pair.Credential);
    }

    public bool TryGetCredential(Dialect dialect, out string credential) =>
        _credentials.TryGetValue(dialect, out credential!);
}
