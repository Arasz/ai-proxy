using System.Net;
using AiProxy.Hosting;
using AiProxy.Hosting.Marker;
using AiProxy.Tests.Integration;

namespace AiProxy.Tests.Unit;

public sealed class SystemProxyMarkerTests
{
    [Fact]
    public void The_endpoint_is_written_and_read_back_by_the_next_owner()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);

        using (var marker = SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero))
        {
            Assert.False(marker.PreviousRunDied);
            Assert.Null(marker.PreviousEndpoint);
            marker.Write(new IPEndPoint(IPAddress.Loopback, ProxyRuntimeOptions.DefaultPort));
            marker.Write(new IPEndPoint(IPAddress.Loopback, 9090));
        }

        using var next = SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero);
        Assert.True(next.PreviousRunDied);
        Assert.Equal("127.0.0.1:9090", next.PreviousEndpoint);
    }

    [Fact]
    public void A_held_marker_cannot_be_acquired_without_waiting()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        using var holder = SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(() => SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero));
    }

    [Fact(Skip = TimingDependent.Reason)]
    public async Task Acquire_waits_for_a_holder_that_lets_go()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var holder = SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero);
        var release = Task.Run(async () =>
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            holder.Dispose();
        }, TestContext.Current.CancellationToken);

        using var marker = SystemProxyMarker.Acquire(directory.Path, TimeSpan.FromSeconds(10));

        await release;
        Assert.NotNull(marker);
    }

    [Fact]
    public void Delete_removes_the_file_so_the_next_owner_sees_no_crash()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var marker = SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero);
        marker.Write(new IPEndPoint(IPAddress.Loopback, ProxyRuntimeOptions.DefaultPort));

        marker.Delete();

        Assert.False(File.Exists(Path.Combine(directory.Path, SystemProxyMarker.FileName)));
        using var next = SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero);
        Assert.False(next.PreviousRunDied);
    }

    [Fact]
    public void Truncate_keeps_the_hold_but_marks_the_proxy_as_restored()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var marker = SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero);
        marker.Write(new IPEndPoint(IPAddress.Loopback, ProxyRuntimeOptions.DefaultPort));

        marker.Truncate();

        Assert.Equal(0, new FileInfo(Path.Combine(directory.Path, SystemProxyMarker.FileName)).Length);
        Assert.Throws<InvalidOperationException>(() => SystemProxyMarker.Acquire(directory.Path, TimeSpan.Zero));
        marker.Dispose();
    }
}
