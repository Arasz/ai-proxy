using AiProxy.Core.Content;

namespace AiProxy.Hosting;

/// <summary>The endpoint that serves back what the proxy compressed.</summary>
public static class ContentEndpoints
{
    /// <summary>The path the original of a compressed request is served from.</summary>
    public const string Route = "/content/{requestId}";

    /// <summary>Maps the endpoint that serves the original of a compressed request.</summary>
    /// <param name="app">The host to map it on.</param>
    public static void MapContentEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(Route, async (
            string requestId,
            IContentStore store,
            CancellationToken cancellationToken) =>
        {
            var record = await store.GetAsync(requestId, cancellationToken);

            if (record is null)
            {
                return Results.Problem(
                    title: "content_not_found",
                    detail: $"no compressed request with the id {requestId} was kept",
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Text(record.Original);
        });
    }
}
