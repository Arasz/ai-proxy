using AiProxy.Hosting;
using AiProxy.Hosting.Integration;

namespace AiProxy.Tests.Unit;

/// <summary>The root certificate is trusted at most once per start, and only when it is not trusted already.</summary>
public sealed class RootCertificateTrustTests
{
    [Fact]
    public void A_trusted_certificate_is_loaded_and_never_trusted_again()
    {
        var root = new RecordingRootCertificate(TrustState.Trusted);

        root.EnsureTrusted();

        Assert.Equal(["load", "check"], root.Calls);
    }

    [Fact]
    public void An_untrusted_certificate_is_trusted_once_after_it_is_loaded()
    {
        var root = new RecordingRootCertificate(TrustState.Untrusted);

        root.EnsureTrusted();

        Assert.Equal(["load", "check", "trust", "check"], root.Calls);
    }

    [Fact]
    public void A_certificate_still_untrusted_after_trusting_stops_the_start()
    {
        var root = new RecordingRootCertificate(TrustState.Untrusted, TrustState.Untrusted);

        var failure = Assert.Throws<InvalidOperationException>(root.EnsureTrusted);

        Assert.Contains("not trusted", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, root.Calls.Count(call => call == "trust"));
    }

    private enum TrustState
    {
        Untrusted,
        Trusted
    }

    private sealed class RecordingRootCertificate(TrustState trustedFromStart, TrustState trustTakes = TrustState.Trusted) : IRootCertificate
    {
        private TrustState _state = trustedFromStart;

        public List<string> Calls { get; } = [];

        public void Load()
        {
            Calls.Add("load");
        }

        public bool IsTrusted()
        {
            Calls.Add("check");
            return _state == TrustState.Trusted;
        }

        public void Trust()
        {
            Calls.Add("trust");
            _state = trustTakes;
        }
    }
}