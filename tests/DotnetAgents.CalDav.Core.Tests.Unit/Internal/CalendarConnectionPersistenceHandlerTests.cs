using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotnetAgents.CalDav.Core.Internal;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarConnectionPersistenceHandlerTests
{
    [Fact]
    public async Task Pooled_sockets_handler_loses_a_put_sent_on_a_connection_an_http10_response_ended()
    {
        await using var server = LoopbackOrigin.Start(LoopbackResponseMode.Http10);
        using var client = new HttpMessageInvoker(CreateSocketsHandler(TimeSpan.FromMinutes(2)));

        using (var read = await client.SendAsync(Request(HttpMethod.Get, server), TestContext.Current.CancellationToken))
            read.StatusCode.ShouldBe(HttpStatusCode.OK);
        var failure = await Should.ThrowAsync<HttpRequestException>(
            () => client.SendAsync(Request(HttpMethod.Put, server), TestContext.Current.CancellationToken));

        failure.HttpRequestError.ShouldBe(HttpRequestError.ResponseEnded);
        server.RequestsOnEndedConnections.ShouldBe(["PUT"]);
    }

    [Fact]
    public async Task Http10_origin_receives_every_request_on_its_own_connection()
    {
        await using var server = LoopbackOrigin.Start(LoopbackResponseMode.Http10);
        using var client = new HttpMessageInvoker(CreateHandler());

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Put })
        {
            using var response = await client.SendAsync(Request(method, server), TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        server.RequestsOnEndedConnections.ShouldBeEmpty();
        server.ConnectionCount.ShouldBe(3);
    }

    [Theory]
    [InlineData(LoopbackResponseMode.Http11)]
    [InlineData(LoopbackResponseMode.Http10KeepAlive)]
    public async Task Persistent_origin_reuses_one_pooled_connection_after_its_first_response(LoopbackResponseMode mode)
    {
        await using var server = LoopbackOrigin.Start(mode);
        using var client = new HttpMessageInvoker(CreateHandler());

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Put })
        {
            using var response = await client.SendAsync(Request(method, server), TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        server.RequestsOnEndedConnections.ShouldBeEmpty();
        server.ConnectionCount.ShouldBe(2);
    }

    [Fact]
    public async Task Disposing_the_handler_disposes_both_connection_handlers()
    {
        var handler = CreateHandler();

        handler.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(() => new HttpMessageInvoker(handler.Pooled, disposeHandler: false)
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:1/"), TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ObjectDisposedException>(() => new HttpMessageInvoker(handler.PerRequest, disposeHandler: false)
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:1/"), TestContext.Current.CancellationToken));
    }

    private static CalendarConnectionPersistenceHandler CreateHandler() => new(
        CreateSocketsHandler(TimeSpan.FromMinutes(2)),
        CreateSocketsHandler(TimeSpan.Zero));

    private static SocketsHttpHandler CreateSocketsHandler(TimeSpan pooledConnectionLifetime) => new()
    {
        UseProxy = false,
        PooledConnectionLifetime = pooledConnectionLifetime,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20)
    };

    private static HttpRequestMessage Request(HttpMethod method, LoopbackOrigin server) => new(method, server.Address)
    {
        Content = method == HttpMethod.Put
            ? new ByteArrayContent("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"u8.ToArray())
            : null
    };

    public enum LoopbackResponseMode
    {
        Http10,
        Http10KeepAlive,
        Http11
    }

    /// <summary>
    /// Answers requests like Radicale's built-in server in <see cref="LoopbackResponseMode.Http10"/> mode,
    /// except that it observes its close after the client could reuse the connection: a request that
    /// arrives on a connection an HTTP/1.0 response ended is recorded and the connection closes unanswered.
    /// </summary>
    private sealed class LoopbackOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly ConcurrentQueue<string> _requestsOnEndedConnections = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly LoopbackResponseMode _mode;
        private Task _accepting = Task.CompletedTask;
        private int _connectionCount;

        private LoopbackOrigin(LoopbackResponseMode mode) => _mode = mode;

        public Uri Address => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/calendar/event.ics");

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public string[] RequestsOnEndedConnections => [.. _requestsOnEndedConnections];

        public static LoopbackOrigin Start(LoopbackResponseMode mode)
        {
            var origin = new LoopbackOrigin(mode);
            origin._listener.Start();
            origin._accepting = origin.AcceptAsync();
            return origin;
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            await Task.WhenAll([_accepting, .. _connections]);
            _stopping.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (await AcceptOrStopAsync() is { } socket)
            {
                Interlocked.Increment(ref _connectionCount);
                _connections.Add(ServeAsync(socket));
            }
        }

        private async Task<Socket?> AcceptOrStopAsync()
        {
            // Stopping the listener faults the pending accept; that ends the accept loop.
            var accept = _listener.AcceptSocketAsync(_stopping.Token).AsTask();
            await ((Task)accept).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return accept.IsCompletedSuccessfully ? accept.Result : null;
        }

        private async Task ServeAsync(Socket socket)
        {
            using var connection = socket;
            // A client that closes its connection, or the test stopping the origin, ends this exchange.
            await ServeRequestsAsync(connection).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        private async Task ServeRequestsAsync(Socket connection)
        {
            await using var stream = new NetworkStream(connection, ownsSocket: false);
            var ended = false;
            while (await ReadRequestAsync(stream, _stopping.Token) is { } method)
            {
                if (ended)
                {
                    _requestsOnEndedConnections.Enqueue(method);
                    return;
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(StatusLine() + "Content-Length: 0\r\n\r\n"), _stopping.Token);
                ended = _mode == LoopbackResponseMode.Http10;
            }
        }

        private string StatusLine() => _mode switch
        {
            LoopbackResponseMode.Http10 => "HTTP/1.0 200 OK\r\n",
            LoopbackResponseMode.Http10KeepAlive => "HTTP/1.0 200 OK\r\nConnection: keep-alive\r\n",
            _ => "HTTP/1.1 200 OK\r\n"
        };

        private static async Task<string?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
        {
            var header = new List<byte>();
            var buffer = new byte[1];
            while (!EndsWithBlankLine(header))
            {
                if (await stream.ReadAsync(buffer, cancellationToken) == 0)
                    return null;
                header.Add(buffer[0]);
            }

            var lines = Encoding.ASCII.GetString([.. header]).Split("\r\n");
            var contentLength = lines
                .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Select(line => int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
                .SingleOrDefault();
            await stream.ReadExactlyAsync(new byte[contentLength], cancellationToken);
            return lines[0].Split(' ')[0];
        }

        private static bool EndsWithBlankLine(List<byte> header) =>
            header.Count >= 4
            && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n';
    }
}
