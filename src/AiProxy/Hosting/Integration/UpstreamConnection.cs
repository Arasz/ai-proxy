using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Http;

namespace AiProxy.Hosting.Integration;

/// <summary>Reads Titanium's internal upstream connection state, which its public API does not expose.</summary>
/// <remarks>A Titanium release that renames these members makes <see cref="IsClosed" /> throw <see cref="MissingMemberException" />.</remarks>
internal static class UpstreamConnection
{
    private const string Connection = "Titanium.Web.Proxy.Network.Tcp.TcpServerConnection, Titanium.Web.Proxy";
    private const string ServerStream = "Titanium.Web.Proxy.Helpers.HttpServerStream, Titanium.Web.Proxy";
    private const string Stream = "Titanium.Web.Proxy.Helpers.HttpStream, Titanium.Web.Proxy";

    /// <summary>Whether a read on the exchange's upstream connection has reached its end, by close or by reset.</summary>
    public static bool IsClosed(HttpWebClient client)
    {
        return HasConnection(client) && IsStreamClosed(GetStream(GetConnection(client)));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_HasConnection")]
    private static extern bool HasConnection(HttpWebClient client);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Connection")]
    [return: UnsafeAccessorType(Connection)]
    private static extern object GetConnection(HttpWebClient client);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Stream")]
    [return: UnsafeAccessorType(ServerStream)]
    private static extern object GetStream([UnsafeAccessorType(Connection)] object connection);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IsClosed")]
    private static extern bool IsStreamClosed([UnsafeAccessorType(Stream)] object stream);
}