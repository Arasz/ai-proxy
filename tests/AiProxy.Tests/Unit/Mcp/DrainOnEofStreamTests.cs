using System.Text;
using AiProxy.Mcp;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;

namespace AiProxy.Tests.Unit.Mcp;

public sealed class DrainOnEofStreamTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private const string Request = """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{}}""";

    [Fact]
    public async Task End_of_input_with_no_request_pending_returns_immediately()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), """{"jsonrpc":"2.0","method":"notifications/initialized"}""" + "\n");

        await DrainAsync(stream).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task End_of_input_waits_until_the_pending_request_is_answered()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), Request + "\n");

        var drain = DrainAsync(stream);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        pending.Complete(new RequestId(7));
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_string_id_is_released_by_the_matching_string_id()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), """{"jsonrpc":"2.0","id":"a-1","method":"ping"}""" + "\n");

        var drain = DrainAsync(stream);
        pending.Complete(new RequestId(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        pending.Complete(new RequestId("a-1"));
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_cancelled_request_no_longer_holds_end_of_input()
    {
        var pending = new PendingRequests();
        var cancel = """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""";
        using var stream = Wrap(pending, new FakeTimeProvider(), Request + "\n" + cancel + "\n");

        await DrainAsync(stream).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task A_line_that_does_not_parse_adds_nothing()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), "not json\n{\"id\":3}\n[1,2]\n");

        await DrainAsync(stream).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task A_last_request_without_a_trailing_newline_is_still_tracked()
    {
        var pending = new PendingRequests();
        using var stream = Wrap(pending, new FakeTimeProvider(), Request);

        var drain = DrainAsync(stream);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        pending.Complete(new RequestId(7));
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task End_of_input_is_released_at_the_bound_when_a_request_never_answers()
    {
        var clock = new TimerObservingTimeProvider();
        var pending = new PendingRequests();
        using var stream = Wrap(pending, clock, Request + "\n");

        var drain = DrainAsync(stream);
        await clock.FirstTimerCreated.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(drain.IsCompleted);

        clock.Advance(Bound);
        await drain.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, pending.Count);
    }

    private static DrainOnEofStream Wrap(PendingRequests pending, TimeProvider clock, string input) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(input)), pending, clock, Bound);

    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[256];
        while (await stream.ReadAsync(buffer, TestContext.Current.CancellationToken) > 0)
        {
        }
    }
}
