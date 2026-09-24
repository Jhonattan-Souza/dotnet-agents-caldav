using System.Collections.Concurrent;
using System.Net;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>
/// Reuses pooled connections only for origins whose last response kept its connection open.
/// </summary>
/// <remarks>
/// An HTTP/1.0 response without the keep-alive connection option ends its connection
/// (RFC 9112 section 9.3); Radicale's built-in server answers every request that way.
/// <see cref="SocketsHttpHandler"/> still returns such a connection to its pool, and a request
/// with content written to it before the server's close is observed fails with
/// <see cref="HttpRequestError.ResponseEnded"/> after the content was sent, so neither the
/// handler nor the write path can retry it. Those origins therefore get one connection per request.
/// </remarks>
internal sealed class CalendarConnectionPersistenceHandler : HttpMessageHandler
{
    private readonly HttpMessageInvoker _pooled;
    private readonly HttpMessageInvoker _perRequest;
    private readonly ConcurrentDictionary<string, bool> _persistentOrigins = new(StringComparer.OrdinalIgnoreCase);

    internal CalendarConnectionPersistenceHandler(SocketsHttpHandler pooled, SocketsHttpHandler perRequest)
    {
        ArgumentNullException.ThrowIfNull(pooled);
        ArgumentNullException.ThrowIfNull(perRequest);

        Pooled = pooled;
        PerRequest = perRequest;
        _pooled = new HttpMessageInvoker(pooled, disposeHandler: true);
        _perRequest = new HttpMessageInvoker(perRequest, disposeHandler: true);
    }

    internal SocketsHttpHandler Pooled { get; }

    internal SocketsHttpHandler PerRequest { get; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var origin = request.RequestUri!.GetLeftPart(UriPartial.Authority);
        var invoker = _persistentOrigins.TryGetValue(origin, out var persistent) && persistent
            ? _pooled
            : _perRequest;
        var response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _persistentOrigins[origin] = KeepsConnectionOpen(response);
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pooled.Dispose();
            _perRequest.Dispose();
        }

        base.Dispose(disposing);
    }

    private static bool KeepsConnectionOpen(HttpResponseMessage response) =>
        response.Version >= HttpVersion.Version11
        || response.Headers.Connection.Contains("keep-alive", StringComparer.OrdinalIgnoreCase);
}
