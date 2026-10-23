using System.Net;
using AiProxy.Hosting;
using AiProxy.Tests.Integration;

namespace AiProxy.Tests.Unit;

public class SystemProxyMarkerTests
{
    [Fact]
    public void The_endpoint_is_written_as_host_and_port_and_survives_a_release_without_delete()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);

        using (var marker = SystemProxyMarker.Acquire(directory.Path))
        {
            Assert.False(marker.PreviousRunDied);
            marker.Write(new IPEndPoint(IPAddress.Loopback, 8080));
            marker.Write(new IPEndPoint(IPAddress.Loopback, 9090));
        }

        Assert.Equal("127.0.0.1:9090", File.ReadAllText(Path.Combine(directory.Path, SystemProxyMarker.FileName)));
        using var next = SystemProxyMarker.Acquire(directory.Path);
        Assert.True(next.PreviousRunDied);
    }

    [Fact]
    public void A_marker_cannot_be_deleted_while_it_is_held_and_can_be_once_released()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var marker = SystemProxyMarker.Acquire(directory.Path);
        marker.Write(new IPEndPoint(IPAddress.Loopback, 8080));

        Assert.False(SystemProxyMarker.TryDelete(directory.Path));
        marker.Dispose();

        Assert.True(SystemProxyMarker.TryDelete(directory.Path));
        Assert.False(File.Exists(Path.Combine(directory.Path, SystemProxyMarker.FileName)));
    }
}
