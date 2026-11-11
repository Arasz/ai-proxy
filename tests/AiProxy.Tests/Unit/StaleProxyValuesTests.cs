using System.Net;
using System.Text.Json.Nodes;
using AiProxy.Hosting.Integration;
using AiProxy.Hosting.Proxy;
using static AiProxy.Tests.Unit.ProxySnapshots;

namespace AiProxy.Tests.Unit;

public sealed class StaleProxyValuesTests
{
    private static readonly IPEndPoint Own = new(IPAddress.Loopback, 8080);
    private static readonly IPEndPoint Dead = new(IPAddress.Loopback, 9);
    private static readonly IPEndPoint[] OwnOnly = [Own];

    private static readonly MacProxy Corporate = MacProxy.On("corp.example", 80);
    private static readonly MacProxy Secure = MacProxy.On("secure.example", 443);
    private static readonly MacProxy Other = MacProxy.On("other.example", 3128);
    private static readonly MacProxy AtOwn = MacProxy.On("127.0.0.1", 8080);

    [Fact]
    public void R1_a_stale_mac_https_takes_the_previous_value_and_an_outside_http_change_is_kept()
    {
        var current = Mac(MacService("Wi-Fi", Other, AtOwn));
        var previous = Mac(MacService("Wi-Fi", Corporate, Secure));

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        Assert.Equal(Other, MacProxyOf(target.Snapshot, "Wi-Fi", "Http"));
        Assert.Equal(Secure, MacProxyOf(target.Snapshot, "Wi-Fi", "Https"));
        Assert.Equal([Own], target.ReplacedEndpoints);
    }

    [Fact]
    public void R2_a_stale_mac_value_without_a_previous_snapshot_is_turned_off()
    {
        var current = Mac(MacService("Wi-Fi", Other, AtOwn));

        var target = StaleProxyValues.Replace(current, OwnOnly, null);

        Assert.False(MacProxyOf(target.Snapshot, "Wi-Fi", "Https").Enabled);
        Assert.Equal(Other, MacProxyOf(target.Snapshot, "Wi-Fi", "Http"));
    }

    [Fact]
    public void R3_a_previous_value_that_is_itself_stale_is_not_copied_back()
    {
        var current = Mac(MacService("Wi-Fi", Other, AtOwn));
        var previous = Mac(MacService("Wi-Fi", Corporate, AtOwn));

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        Assert.False(MacProxyOf(target.Snapshot, "Wi-Fi", "Https").Enabled);
    }

    [Fact]
    public void R3_a_stale_previous_windows_entry_is_not_copied_back()
    {
        var current = WindowsSnapshot(1, "http=127.0.0.1:8080;https=127.0.0.1:8080");
        var previous = WindowsSnapshot(1, "http=127.0.0.1:8080;https=secure.example:443");

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        Assert.True(JsonNode.DeepEquals(Field(WindowsSnapshot(1, "https=secure.example:443"), "originalValues"), Field(target.Snapshot, "originalValues")));
    }

    [Fact]
    public void R3_a_stale_previous_gnome_host_is_emptied_and_the_mode_stays_manual()
    {
        var current = Linux(Gnome("manual", "127.0.0.1", 8080, "127.0.0.1", 8080));
        var previous = Linux(Gnome("manual", "127.0.0.1", 8080, "secure.example", 443));

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        var gnome = Field(target.Snapshot, "_gnome")!;
        Assert.Equal("manual", gnome["Mode"]!.GetValue<string>());
        Assert.Equal("", gnome["HttpHost"]!.GetValue<string>());
        Assert.Equal("secure.example", gnome["HttpsHost"]!.GetValue<string>());
    }

    [Fact]
    public void R3_a_stale_previous_kde_proxy_is_emptied()
    {
        var current = Linux(null, Kde("1", "http://127.0.0.1:8080", "http://127.0.0.1:8080"));
        var previous = Linux(null, Kde("1", "http://localhost:8080", "http://secure.example:443"));

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        var kde = Field(target.Snapshot, "_kde")!;
        Assert.Equal("", kde["HttpProxy"]!.GetValue<string>());
        Assert.Equal("http://secure.example:443", kde["HttpsProxy"]!.GetValue<string>());
        Assert.Equal("1", kde["ProxyType"]!.GetValue<string>());
    }

    [Fact]
    public void R4_settings_with_nothing_stale_are_left_as_captured()
    {
        var current = Mac(MacService("Wi-Fi", Corporate, Secure));

        AssertKept(current, StaleProxyValues.Replace(current, OwnOnly, null));
    }

    [Theory]
    [InlineData("127.0.0.1", 3128)]
    [InlineData("10.0.0.2", 8080)]
    public void R5_a_local_proxy_on_another_port_or_a_lan_proxy_on_the_same_port_is_kept(string host, int port)
    {
        var current = Mac(MacService("Wi-Fi", Corporate, MacProxy.On(host, port)));

        AssertKept(current, StaleProxyValues.Replace(current, OwnOnly, null));
    }

    [Fact]
    public void R6_mac_services_are_matched_by_name_not_by_position()
    {
        var current = Mac(MacService("Ethernet", Corporate, Secure), MacService("Wi-Fi", Other, AtOwn));
        var previous = Mac(MacService("Wi-Fi", Corporate, MacProxy.On("wifi.example", 443)), MacService("Ethernet", Corporate, Secure));

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        Assert.Equal(MacProxy.On("wifi.example", 443), MacProxyOf(target.Snapshot, "Wi-Fi", "Https"));
        Assert.Equal(Secure, MacProxyOf(target.Snapshot, "Ethernet", "Https"));
    }

    [Fact]
    public void R7_one_stale_gnome_protocol_is_replaced_and_the_mode_stays_manual()
    {
        var current = Linux(Gnome("manual", "corp.example", 80, "127.0.0.1", 8080));
        var previous = Linux(Gnome("manual", "corp.example", 80, "secure.example", 443));

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        var gnome = Field(target.Snapshot, "_gnome")!;
        Assert.Equal("manual", gnome["Mode"]!.GetValue<string>());
        Assert.Equal("corp.example", gnome["HttpHost"]!.GetValue<string>());
        Assert.Equal("secure.example", gnome["HttpsHost"]!.GetValue<string>());
        Assert.Equal(443, gnome["HttpsPort"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("none")]
    public void R7b_gnome_hosts_that_point_at_a_stale_endpoint_outside_manual_mode_are_inert(string mode)
    {
        var current = Linux(Gnome(mode, "127.0.0.1", 8080, "127.0.0.1", 8080));

        AssertKept(current, StaleProxyValues.Replace(current, OwnOnly, null));
    }

    [Fact]
    public void R8_gnome_with_both_protocols_stale_and_no_previous_snapshot_is_turned_off()
    {
        var current = Linux(Gnome("manual", "127.0.0.1", 8080, "127.0.0.1", 8080));

        var target = StaleProxyValues.Replace(current, OwnOnly, null);

        var gnome = Field(target.Snapshot, "_gnome")!;
        Assert.Equal("none", gnome["Mode"]!.GetValue<string>());
        Assert.Equal("", gnome["HttpHost"]!.GetValue<string>());
        Assert.Equal(0, gnome["HttpsPort"]!.GetValue<int>());
        Assert.False(gnome["HttpEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void R9_kde_with_both_protocols_restored_from_an_active_previous_takes_the_previous_proxies_in_manual_mode()
    {
        var current = Linux(null, Kde("1", "http://127.0.0.1:8080", "http://127.0.0.1:8080"));
        var previous = Linux(null, Kde("1", "http://corp.example:80", "http://secure.example:443"));

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        var kde = Field(target.Snapshot, "_kde")!;
        Assert.Equal("1", kde["ProxyType"]!.GetValue<string>());
        Assert.Equal("http://corp.example:80", kde["HttpProxy"]!.GetValue<string>());
        Assert.Equal("http://secure.example:443", kde["HttpsProxy"]!.GetValue<string>());
    }

    [Fact]
    public void R10_a_stale_windows_https_entry_takes_the_previous_one_with_titaniums_derived_fields()
    {
        var current = WindowsSnapshot(1, "http=corp.example:80;https=127.0.0.1:9");
        var previous = WindowsSnapshot(1, "http=corp.example:80;https=secure.example:443");

        var target = StaleProxyValues.Replace(current, [Dead], previous);

        Assert.True(JsonNode.DeepEquals(Field(previous, "originalValues"), Field(target.Snapshot, "originalValues")));
    }

    [Fact]
    public void R11_windows_with_every_entry_stale_and_no_previous_snapshot_is_turned_off()
    {
        var current = WindowsSnapshot(1, "http=127.0.0.1:8080;https=127.0.0.1:8080");

        var target = StaleProxyValues.Replace(current, OwnOnly, null);

        Assert.True(JsonNode.DeepEquals(Field(WindowsSnapshot(0, ""), "originalValues"), Field(target.Snapshot, "originalValues")));
    }

    [Theory]
    [InlineData("127.0.0.1:8080", null, "")]
    [InlineData("127.0.0.1:8080", "corp.example:80", "corp.example:80")]
    [InlineData("127.0.0.1:8080", "http=corp.example:80;https=secure.example:443", "http=corp.example:80;https=secure.example:443")]
    [InlineData("127.0.0.1:8080", "http=corp.example:80;https=secure.example:443;ftp=ftp.example:21", "http=corp.example:80;https=secure.example:443")]
    public void R12_a_stale_unnamed_windows_entry_takes_the_previous_unnamed_entry_or_the_named_ones_it_covered(
        string proxyServer,
        string? previousProxyServer,
        string expected)
    {
        var current = WindowsSnapshot(1, proxyServer);
        var previous = previousProxyServer is null ? null : WindowsSnapshot(1, previousProxyServer);

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        var expectedEnable = expected.Length == 0 ? 0 : 1;
        Assert.True(JsonNode.DeepEquals(Field(WindowsSnapshot(expectedEnable, expected), "originalValues"), Field(target.Snapshot, "originalValues")));
    }

    [Fact]
    public void R13_values_pointing_at_two_stale_endpoints_on_different_ports_are_both_replaced_and_reported()
    {
        var current = Mac(MacService("Wi-Fi", MacProxy.On("127.0.0.1", 9), AtOwn));

        var target = StaleProxyValues.Replace(current, [Own, Dead], null);

        Assert.False(MacProxyOf(target.Snapshot, "Wi-Fi", "Http").Enabled);
        Assert.False(MacProxyOf(target.Snapshot, "Wi-Fi", "Https").Enabled);
        Assert.Equal([Dead, Own], target.ReplacedEndpoints);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("linux")]
    public void R14_a_previous_snapshot_from_another_backend_or_unparseable_falls_back_to_off(string kind)
    {
        var current = Mac(MacService("Wi-Fi", Other, AtOwn));
        var previous = kind == "linux" ? Linux(Gnome("manual", "corp.example", 80, "secure.example", 443)) : kind;

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        Assert.False(MacProxyOf(target.Snapshot, "Wi-Fi", "Https").Enabled);
    }

    [Fact]
    public void R15_a_disabled_mac_proxy_left_pointing_at_a_stale_endpoint_is_inert()
    {
        var current = Mac(MacService("Wi-Fi", Corporate, AtOwn with { Enabled = false }));

        AssertKept(current, StaleProxyValues.Replace(current, OwnOnly, null));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "http://pac.example/proxy.pac")]
    public void R16_an_inactive_windows_proxy_left_pointing_at_a_stale_endpoint_is_inert(int proxyEnable, string? autoConfigUrl)
    {
        var current = WindowsSnapshot(proxyEnable, "http=127.0.0.1:8080;https=127.0.0.1:8080", autoConfigUrl);

        AssertKept(current, StaleProxyValues.Replace(current, OwnOnly, null));
    }

    [Fact]
    public void R17_kde_values_left_pointing_at_a_stale_endpoint_outside_manual_mode_are_inert()
    {
        var current = Linux(null, Kde("0", "http://127.0.0.1:8080", "http://127.0.0.1:8080"));

        AssertKept(current, StaleProxyValues.Replace(current, OwnOnly, null));
    }

    [Theory]
    [InlineData("unknown-backend")]
    [InlineData("missing-field")]
    [InlineData("not-json")]
    public void R18_an_unexpected_capture_is_kept_and_reported_as_not_cleaned_instead_of_failing_the_start(string kind)
    {
        var service = MacService("Wi-Fi", Other, AtOwn);
        service.Remove("HttpsPort");
        var current = kind switch
        {
            "unknown-backend" => Snapshot("Titanium.Web.Proxy.Helpers.FutureBackend", new Dictionary<string, object?> { ["_original"] = new[] { MacService("Wi-Fi", Other, AtOwn) } }),
            "missing-field" => Mac(service),
            _ => "not json"
        };

        AssertNotCleaned(current, StaleProxyValues.Replace(current, OwnOnly, null));
    }

    [Fact]
    public void A_stale_named_windows_entry_without_a_previous_named_one_takes_the_previous_unnamed_address()
    {
        var current = WindowsSnapshot(1, "http=user.example:80;https=127.0.0.1:8080");
        var previous = WindowsSnapshot(1, "corp.example:3128");

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        AssertWindowsValues(WindowsSnapshot(1, "http=user.example:80;https=corp.example:3128"), target.Snapshot);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "http://pac.example/proxy.pac")]
    public void An_inactive_previous_windows_proxy_is_not_copied_back(int previousEnable, string? previousAutoConfigUrl)
    {
        var current = WindowsSnapshot(1, "http=corp.example:80;https=127.0.0.1:8080");
        var previous = WindowsSnapshot(previousEnable, "http=old.example:80;https=leftover.example:443", previousAutoConfigUrl);

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        AssertWindowsValues(WindowsSnapshot(1, "http=corp.example:80"), target.Snapshot);
    }

    [Fact]
    public void An_inactive_previous_unnamed_windows_entry_is_not_copied_back_and_the_proxy_turns_off()
    {
        var current = WindowsSnapshot(1, "127.0.0.1:8080");
        var previous = WindowsSnapshot(0, "leftover.example:3128");

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        AssertWindowsValues(WindowsSnapshot(0, ""), target.Snapshot);
    }

    [Theory]
    [InlineData("ftp=ftp.example:21;127.0.0.1:8080", "ftp=ftp.example:21")]
    [InlineData("socks=socks.example:1080;http=127.0.0.1:8080;https=127.0.0.1:8080", "socks=socks.example:1080")]
    public void A_windows_proxy_left_with_no_http_or_https_entry_is_turned_off_as_titaniums_remove_does(string proxyServer, string expected)
    {
        var current = WindowsSnapshot(1, proxyServer);

        var target = StaleProxyValues.Replace(current, OwnOnly, null);

        AssertWindowsValues(WindowsSnapshot(0, expected), target.Snapshot);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("auto")]
    public void An_inactive_previous_gnome_proxy_is_not_copied_back_and_both_stale_protocols_turn_the_mode_off(string previousMode)
    {
        var current = Linux(Gnome("manual", "127.0.0.1", 8080, "127.0.0.1", 8080));
        var previous = Linux(Gnome(previousMode, "old.example", 80, "leftover.example", 443));

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        var gnome = Field(target.Snapshot, "_gnome")!;
        Assert.Equal("none", gnome["Mode"]!.GetValue<string>());
        Assert.Equal("", gnome["HttpHost"]!.GetValue<string>());
        Assert.Equal("", gnome["HttpsHost"]!.GetValue<string>());
    }

    [Fact]
    public void An_inactive_previous_gnome_host_is_not_copied_into_a_manual_proxy_that_stays_on()
    {
        var current = Linux(Gnome("manual", "corp.example", 80, "127.0.0.1", 8080));
        var previous = Linux(Gnome("none", "old.example", 80, "leftover.example", 443));

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        var gnome = Field(target.Snapshot, "_gnome")!;
        Assert.Equal("manual", gnome["Mode"]!.GetValue<string>());
        Assert.Equal("corp.example", gnome["HttpHost"]!.GetValue<string>());
        Assert.Equal("", gnome["HttpsHost"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    public void An_inactive_previous_kde_proxy_is_not_copied_back_and_both_stale_protocols_turn_the_type_off(string previousType)
    {
        var current = Linux(null, Kde("1", "http://127.0.0.1:8080", "http://127.0.0.1:8080"));
        var previous = Linux(null, Kde(previousType, "http://old.example:80", "http://leftover.example:443"));

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        var kde = Field(target.Snapshot, "_kde")!;
        Assert.Equal("0", kde["ProxyType"]!.GetValue<string>());
        Assert.Equal("", kde["HttpProxy"]!.GetValue<string>());
        Assert.Equal("", kde["HttpsProxy"]!.GetValue<string>());
    }

    [Fact]
    public void A_switched_off_previous_mac_proxy_is_restored_switched_off()
    {
        var current = Mac(MacService("Wi-Fi", Other, AtOwn));
        var previous = Mac(MacService("Wi-Fi", Corporate, Secure with { Enabled = false }));

        var target = StaleProxyValues.Replace(current, OwnOnly, previous);

        Assert.False(MacProxyOf(target.Snapshot, "Wi-Fi", "Https").Enabled);
        Assert.Equal(Other, MacProxyOf(target.Snapshot, "Wi-Fi", "Http"));
    }

    [Theory]
    [InlineData("type-load")]
    [InlineData("missing-method")]
    public void R18_a_windows_capture_titanium_can_no_longer_re_encode_does_not_fail_the_start(string kind)
    {
        var current = WindowsSnapshot(1, "http=corp.example:80;https=127.0.0.1:8080");
        Exception failure = kind == "type-load"
            ? new TypeLoadException("Titanium.Web.Proxy.Helpers.ProxyInfo")
            : new MissingMethodException("Titanium.Web.Proxy.Helpers.ProxyInfo", ".ctor");

        var target = StaleProxyValues.Replace(current, OwnOnly, null, () => throw failure);

        AssertNotCleaned(current, target);
        Assert.Same(failure, target.CleanFailure);
    }

    [Fact]
    public void A_mac_proxy_at_localhost_on_the_stale_port_points_at_the_stale_endpoint()
    {
        var current = Mac(MacService("Wi-Fi", Corporate, MacProxy.On("localhost", 8080)));

        var target = StaleProxyValues.Replace(current, OwnOnly, null);

        Assert.False(MacProxyOf(target.Snapshot, "Wi-Fi", "Https").Enabled);
    }

    [Fact]
    public void A_windows_entry_at_the_ipv6_loopback_on_the_stale_port_points_at_the_stale_endpoint()
    {
        var current = WindowsSnapshot(1, "http=corp.example:80;https=[::1]:8080");

        var target = StaleProxyValues.Replace(current, OwnOnly, null);

        Assert.True(JsonNode.DeepEquals(Field(WindowsSnapshot(1, "http=corp.example:80"), "originalValues"), Field(target.Snapshot, "originalValues")));
    }

    [Fact]
    public void A_kde_proxy_at_localhost_on_the_stale_port_points_at_the_stale_endpoint()
    {
        var current = Linux(null, Kde("1", "http://corp.example:80", "http://localhost:8080"));

        var target = StaleProxyValues.Replace(current, OwnOnly, null);

        var kde = Field(target.Snapshot, "_kde")!;
        Assert.Equal("", kde["HttpsProxy"]!.GetValue<string>());
        Assert.Equal("1", kde["ProxyType"]!.GetValue<string>());
    }

    [Fact]
    public void A_proxy_at_the_stale_endpoints_own_non_loopback_address_points_at_it()
    {
        var lan = new IPEndPoint(IPAddress.Parse("192.168.1.5"), 8080);
        var current = Mac(MacService("Wi-Fi", Corporate, MacProxy.On("192.168.1.5", 8080)));

        var target = StaleProxyValues.Replace(current, [lan], null);

        Assert.Equal([lan], target.ReplacedEndpoints);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not an endpoint")]
    public void The_stale_set_is_the_own_endpoint_alone_when_the_previous_endpoint_is_missing_or_unparseable(string? previousEndpoint)
    {
        Assert.Equal([Own], StaleProxyValues.Endpoints(Own, previousEndpoint));
    }

    [Fact]
    public void The_stale_set_holds_the_previous_endpoint_and_the_own_endpoint()
    {
        Assert.Equal([Dead, Own], StaleProxyValues.Endpoints(Own, "127.0.0.1:9"));
    }

    /// <summary>Asserts the Windows <c>originalValues</c> of two snapshots are equal, derived fields included.</summary>
    private static void AssertWindowsValues(string expected, string actual) =>
        Assert.Equal(Field(expected, "originalValues")!.ToJsonString(), Field(actual, "originalValues")!.ToJsonString());

    /// <summary>Asserts <paramref name="target" /> is the capture as taken, with nothing stale found and no failure.</summary>
    private static void AssertKept(string current, RestoreTarget target)
    {
        Assert.Equal(current, target.Snapshot);
        Assert.Empty(target.ReplacedEndpoints);
        Assert.Null(target.CleanFailure);
    }

    /// <summary>Asserts <paramref name="target" /> is the capture as taken because cleaning failed.</summary>
    private static void AssertNotCleaned(string current, RestoreTarget target)
    {
        Assert.Equal(current, target.Snapshot);
        Assert.Empty(target.ReplacedEndpoints);
        Assert.NotNull(target.CleanFailure);
    }
}
