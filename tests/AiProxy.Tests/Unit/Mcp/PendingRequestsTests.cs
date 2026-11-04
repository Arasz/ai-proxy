using AiProxy.Mcp;
using ModelContextProtocol.Protocol;

namespace AiProxy.Tests.Unit.Mcp;

public sealed class PendingRequestsTests
{
    [Fact]
    public void A_string_id_is_not_released_by_the_same_number()
    {
        var pending = new PendingRequests();
        pending.Track("""{"id":"7","method":"ping"}""");

        pending.Complete(new RequestId(7));

        Assert.Equal(1, pending.Count);
    }

    [Fact]
    public void A_number_id_is_not_released_by_the_same_string()
    {
        var pending = new PendingRequests();
        pending.Track("""{"id":7,"method":"ping"}""");

        pending.Complete(new RequestId("7"));

        Assert.Equal(1, pending.Count);
    }

    [Fact]
    public void A_cancel_for_an_unknown_id_changes_nothing()
    {
        var pending = new PendingRequests();
        pending.Track("""{"id":7,"method":"ping"}""");

        pending.Track("""{"method":"notifications/cancelled","params":{"requestId":99}}""");

        Assert.Equal([new RequestId(7)], pending.Ids);
    }

    [Fact]
    public void Completing_every_request_completes_the_empty_signal()
    {
        var pending = new PendingRequests();
        pending.Track("""{"id":1,"method":"a"}""");
        pending.Track("""{"id":2,"method":"b"}""");
        Assert.False(pending.WhenEmpty.IsCompleted);

        pending.Complete(new RequestId(1));
        pending.Complete(new RequestId(2));

        Assert.True(pending.WhenEmpty.IsCompleted);
    }

    [Fact]
    public async Task A_reply_releases_its_id_even_when_sending_it_fails()
    {
        var pending = new PendingRequests();
        pending.Track("""{"id":7,"method":"ping"}""");
        var reply = new JsonRpcResponse { Id = new RequestId(7), Result = null };

        await Assert.ThrowsAsync<IOException>(() => pending.ReleaseAfterAsync(reply, () => throw new IOException("stdout closed")));

        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public async Task A_notification_sent_does_not_release_a_request()
    {
        var pending = new PendingRequests();
        pending.Track("""{"id":7,"method":"ping"}""");

        await pending.ReleaseAfterAsync(new JsonRpcNotification { Method = "notifications/message" }, () => Task.CompletedTask);

        Assert.Equal(1, pending.Count);
    }
}
