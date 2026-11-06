using AiProxy.Hosting.Integration;

namespace AiProxy.Tests.Unit;

public sealed class SystemProxySnapshotCodecTests
{
    [Fact]
    public void Private_record_snapshots_encode()
    {
        var source = new SnapshotFixture("manual", null, ProxyKind.Http, [], new Dictionary<string, string?>());

        var json = SystemProxySnapshotCodec.Write(source, typeof(SnapshotFixture));

        Assert.Contains("manual", json);
    }

    [Fact]
    public void Private_record_snapshots_round_trip_with_nullable_values_enums_and_collections()
    {
        var source = new SnapshotFixture(
            "manual",
            null,
            ProxyKind.Http | ProxyKind.Https,
            [new ServiceFixture("Wi-Fi", true, 8080)],
            new Dictionary<string, string?> { ["http_proxy"] = "http://127.0.0.1:8080", ["https_proxy"] = null });

        var json = SystemProxySnapshotCodec.Write(source, typeof(SnapshotFixture));
        var restored = Assert.IsType<SnapshotFixture>(SystemProxySnapshotCodec.Read(json, typeof(SnapshotFixture)));

        Assert.Equal(source.Mode, restored.Mode);
        Assert.Equal(source.OptionalPort, restored.OptionalPort);
        Assert.Equal(source.Kind, restored.Kind);
        Assert.Equal(source.Services, restored.Services);
        Assert.Equal(source.Environment, restored.Environment);
    }

    [Flags]
    private enum ProxyKind
    {
        Http = 1,
        Https = 2
    }

    private sealed record ServiceFixture(string Name, bool Enabled, int Port);

    private sealed record SnapshotFixture(
        string Mode,
        int? OptionalPort,
        ProxyKind Kind,
        List<ServiceFixture> Services,
        Dictionary<string, string?> Environment);
}
