using System.Net;
using AiProxy.Hosting.Integration;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace AiProxy.Tests.Unit;

public sealed class TitaniumSystemProxyPlatformTests
{
    private static readonly ExplicitProxyEndPoint EndPoint = new(IPAddress.Loopback, 18080, false);

    [Fact]
    public void ApplyWith_returns_the_read_back_settings_and_keeps_them_when_setting_succeeds()
    {
        var calls = new Calls();

        var applied = TitaniumSystemProxyPlatform.ApplyWith(
            EndPoint,
            endPoint => calls.Set(endPoint, SystemProxyChangeResult.Ok("set")),
            calls.Restore,
            () => calls.Read("read-back-settings"));

        Assert.Equal("read-back-settings", applied);
        Assert.Same(EndPoint, calls.SetEndPoint);
        Assert.Equal(0, calls.Restores);
        Assert.Equal(1, calls.Reads);
    }

    [Fact]
    public void ApplyWith_restores_once_and_reports_titanium_message_without_reading_back_when_setting_fails()
    {
        var calls = new Calls();

        var exception = Assert.Throws<InvalidOperationException>(() => TitaniumSystemProxyPlatform.ApplyWith(
            EndPoint,
            endPoint => calls.Set(endPoint, SystemProxyChangeResult.Fail("networksetup denied")),
            calls.Restore,
            () => calls.Read("must-not-be-read")));

        Assert.Contains("networksetup denied", exception.Message);
        Assert.Equal(1, calls.Restores);
        Assert.Equal(0, calls.Reads);
    }

    [Fact]
    public void ApplyWith_restores_once_and_keeps_the_read_failure_as_inner_exception_when_read_back_throws()
    {
        var calls = new Calls();
        var readFailure = new IOException("scutil unavailable");

        var exception = Assert.Throws<InvalidOperationException>(() => TitaniumSystemProxyPlatform.ApplyWith(
            EndPoint,
            endPoint => calls.Set(endPoint, SystemProxyChangeResult.Ok("set")),
            calls.Restore,
            () => throw readFailure));

        Assert.Same(readFailure, exception.InnerException);
        Assert.Contains("scutil unavailable", exception.Message);
        Assert.Equal(1, calls.Restores);
    }

    /// <summary>Records what the apply logic asked of the platform.</summary>
    private sealed class Calls
    {
        public ExplicitProxyEndPoint? SetEndPoint { get; private set; }

        public int Restores { get; private set; }

        public int Reads { get; private set; }

        public SystemProxyChangeResult Set(ExplicitProxyEndPoint endPoint, SystemProxyChangeResult result)
        {
            SetEndPoint = endPoint;
            return result;
        }

        public void Restore() => Restores++;

        public string Read(string value)
        {
            Reads++;
            return value;
        }
    }
}
