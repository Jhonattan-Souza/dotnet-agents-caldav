using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Mcp.Tools;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

/// <summary>Exercises raw JSON evidence that the SDK's dictionary client cannot represent.</summary>
public sealed class CalendarMcpRawStdioTests
{
    [Theory]
    [InlineData("events.create", "event", "private-event-marker")]
    [InlineData("todos.create", "todo", "private-todo-marker")]
    public async Task CalendarEntityCreate_AmbiguousPutIsReconciledOnceAndRedactedOverRawStdio(
        string toolName,
        string entityKind,
        string privateMarker)
    {
        await using var server = new AmbiguousCreateServer();
        var fields = entityKind == "event"
            ? "{\"summary\":\"" + privateMarker
                + "\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"}}"
            : "{\"summary\":\"" + privateMarker + "\"}";
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\""
            + toolName
            + "\",\"arguments\":{\"destination\":{\"mode\":\"selected\",\"calendar\":{\"by\":\"href\",\"href\":\""
            + server.CalendarHref
            + "\"}},\"entity\":{\"kind\":\""
            + entityKind
            + "\",\"uid\":\"ambiguous-"
            + entityKind
            + "\",\"fields\":"
            + fields
            + "}}}}";

        var result = await InvokeRawAsync(request, server.BaseUrl, server.CalendarHref);

        AssertTypedError(result, "indeterminate", "postWriteVerificationOrReconciliation");
        result.GetProperty("structuredContent").GetProperty("mutationState").GetString().ShouldBe("unknown");
        result.ToString().ShouldNotContain(privateMarker);
        server.PutCount.ShouldBe(1);
        server.GetCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("events.create", "{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\"event\",\"fields\":{\"summary\":\"private-event-marker\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"}}}}", "private-event-marker")]
    [InlineData("todos.create", "{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\"todo\",\"fields\":{\"summary\":\"private-todo-marker\"}}}", "private-todo-marker")]
    public async Task CalendarEntityCreate_DiscoveryFailureReturnsTypedSelectionFailureWithoutWriting(
        string toolName,
        string arguments,
        string privateMarker)
    {
        await using var server = new AmbiguousCreateServer(failDiscovery: true);
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\""
            + toolName
            + "\",\"arguments\":"
            + arguments
            + "}}";

        var result = await InvokeRawAsync(request, server.BaseUrl, server.CalendarHref);

        AssertTypedError(result, "upstream_unavailable", "selectionDiscoveryCapability");
        result.GetProperty("structuredContent").GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        result.ToString().ShouldNotContain(privateMarker);
        result.ToString().ShouldNotContain("private-upstream-marker");
        server.PutCount.ShouldBe(0);
    }

    [Fact]
    public async Task EventCreate_RootDuplicateArgumentsReturnTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"events.create","arguments":{"destination":{"mode":"default"},"destination":{"mode":"selected","calendar":{"by":"name","name":"Secret"}},"entity":{"kind":"event","fields":{}}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
        result.ToString().ShouldNotContain("Secret");
    }

    [Fact]
    public async Task EventCreate_MalformedRecurrenceInputReturnsTypedInvalidInputWithoutEchoingContent()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"events.create","arguments":{"destination":{"mode":"default"},"entity":{"kind":"event","fields":{"description":"private-marker","recurrenceSet":{"rrule":null}}}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
        result.ToString().ShouldNotContain("private-marker");
    }

    [Fact]
    public async Task CalendarOccurrenceQuery_NormalRawCallReachesServiceAndReturnsTypedExecutionFailure()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_occurrences.query","arguments":{"scope":{"mode":"default"},"from":{"kind":"utcDateTime","value":"2026-08-15T12:00:00Z"},"to":{"kind":"utcDateTime","value":"2026-08-16T12:00:00Z"}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "upstream_unavailable", "execution");
    }

    [Fact]
    public async Task CalendarOccurrenceQuery_RootDuplicateArgumentsReturnTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_occurrences.query","arguments":{"scope":{"mode":"default"},"scope":{"mode":"all"},"from":{"kind":"utcDateTime","value":"2026-08-15T12:00:00Z"},"to":{"kind":"utcDateTime","value":"2026-08-16T12:00:00Z"}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
    }

    [Fact]
    public async Task CalendarEntityQuery_NormalInvalidArgumentsReturnTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_entities.query","arguments":{"scope":{"mode":"default"},"entityKinds":["event"],"unknown":true}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
    }

    [Fact]
    public async Task CalendarEntityQuery_SelectedNameWithInternalSpacesReachesTheService()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_entities.query","arguments":{"scope":{"mode":"selected","calendar":{"by":"name","name":"No such authorized calendar"}},"entityKinds":["todo"]}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "upstream_unavailable", "execution");
    }

    [Fact]
    public async Task CalendarEntityQuery_RootDuplicateArgumentsReturnTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_entities.query","arguments":{"scope":{"mode":"default"},"scope":{"mode":"all"},"entityKinds":["event"]}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
    }

    [Fact]
    public async Task CalendarEntityQuery_OversizedDuplicateArgumentsPreferPayloadAdmissionFailure()
    {
        var padding = new string('x', CalendarEntityTools.MaximumArgumentBytes);
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"calendar_entities.query\",\"arguments\":{\"scope\":{\"mode\":\"default\"},\"scope\":{\"mode\":\"all\"},\"entityKinds\":[\"event\"],\"padding\":\""
            + padding
            + "\"}}}";

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "payload_too_large", "admissionAndPayload");
    }

    private static async Task<JsonElement> InvokeRawAsync(
        string toolRequest,
        string baseUrl = "http://127.0.0.1:1",
        string calendarHref = "http://127.0.0.1:1/calendars/test/")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var process = StartServer(baseUrl, calendarHref);
        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"raw-test\",\"version\":\"1\"}}}");
            await process.StandardInput.FlushAsync(timeout.Token);
            _ = await ReadResponseAsync(process, 1, timeout.Token);
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.WriteLineAsync(toolRequest);
            await process.StandardInput.FlushAsync(timeout.Token);
            var response = await ReadResponseAsync(process, 2, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            (await process.StandardError.ReadToEndAsync(timeout.Token)).ShouldBeEmpty();
            return response.GetProperty("result").Clone();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static Process StartServer(string baseUrl, string calendarHref)
    {
        var startInfo = new ProcessStartInfo("dotnet", GetServerAssemblyPath())
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["CALDAV_URL"] = baseUrl;
        startInfo.Environment["CALDAV_USERNAME"] = "test";
        startInfo.Environment["CALDAV_PASSWORD"] = "test";
        startInfo.Environment["CALDAV_CALENDAR_HREFS"] = calendarHref;
        return Process.Start(startInfo)!;
    }

    private static async Task<JsonElement> ReadResponseAsync(
        Process process,
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == expectedId)
                return document.RootElement.Clone();
        }
        throw new InvalidOperationException("The MCP server closed stdout before returning the expected response.");
    }

    private static void AssertTypedError(JsonElement result, string code, string phase)
    {
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().ShouldBe(code);
        structured.GetProperty("phase").GetString().ShouldBe(phase);
        structured.TryGetProperty("items", out _).ShouldBeFalse();
    }

    private static string GetServerAssemblyPath()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory,
                "src",
                "DotnetAgents.CalDav.Mcp",
                "bin",
                "Release",
                "net10.0",
                "DotnetAgents.CalDav.Mcp.dll");
            if (File.Exists(candidate))
                return candidate;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new FileNotFoundException("Could not locate the built MCP server assembly.");
    }

    private sealed class AmbiguousCreateServer : IAsyncDisposable
    {
        private readonly bool _failDiscovery;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serve;
        private int _getCount;
        private int _putCount;

        public AmbiguousCreateServer(bool failDiscovery = false)
        {
            _failDiscovery = failDiscovery;
            var port = ReservePort();
            BaseUrl = $"http://127.0.0.1:{port}/";
            CalendarHref = BaseUrl + "calendars/test/entities/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }

        public string CalendarHref { get; }

        public int GetCount => Volatile.Read(ref _getCount);

        public int PutCount => Volatile.Read(ref _putCount);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            finally
            {
                _listener.Close();
                _stopping.Dispose();
            }
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    await RespondAsync(context);
                }
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            var method = context.Request.HttpMethod;
            if (method == "PROPFIND")
            {
                if (_failDiscovery)
                {
                    var bytes = Encoding.UTF8.GetBytes("private-upstream-marker");
                    context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
                    context.Response.Close();
                    return;
                }
                var discovery = context.Request.Url!.AbsolutePath == "/"
                    ? "<d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>/calendars/test/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                    : "<d:response><d:href>/calendars/test/entities/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><d:displayname>Entities</d:displayname><c:supported-calendar-component-set><c:comp name=\"VEVENT\"/><c:comp name=\"VTODO\"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
                await WriteXmlAsync(context.Response, discovery);
                return;
            }
            if (method == "REPORT")
            {
                await WriteXmlAsync(context.Response, string.Empty);
                return;
            }
            if (method == "PUT")
            {
                Interlocked.Increment(ref _putCount);
                context.Response.StatusCode = (int)HttpStatusCode.RequestTimeout;
                context.Response.Close();
                return;
            }
            if (method == "GET")
            {
                Interlocked.Increment(ref _getCount);
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }
            context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            context.Response.Close();
        }

        private static async Task WriteXmlAsync(HttpListenerResponse response, string body)
        {
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
                + body
                + "</d:multistatus>";
            var bytes = Encoding.UTF8.GetBytes(xml);
            response.StatusCode = (int)HttpStatusCode.MultiStatus;
            response.ContentType = "application/xml; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
            response.Close();
        }

        private static int ReservePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
