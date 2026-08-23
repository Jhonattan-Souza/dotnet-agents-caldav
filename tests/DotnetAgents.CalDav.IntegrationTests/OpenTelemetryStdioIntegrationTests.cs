using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

[Collection("RadicaleCollection")]
public sealed class OpenTelemetryStdioIntegrationTests
{
    private readonly RadicaleFixture _fixture;

    public OpenTelemetryStdioIntegrationTests(RadicaleFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OptIn_ExportsSafeParentedWaterfallLogsAndMcpMetricsOverLoopbackOtlp()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var recurringUid = $"private-recurring-{suffix}";
        var createdUid = $"private-created-{suffix}";
        var privateSummary = $"Private telemetry summary {suffix}";
        var privateCalendarName = $"Private Calendar Name {suffix}";
        var recurringHref = await PutResourceAsync(
            _fixture.TodoCalendarHref,
            $"{recurringUid}.ics",
            RecurringTodo(recurringUid));
        string? createdHref = null;
        string? createdEntityTag = null;
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        try
        {
            string? nextCursor;
            await using (var client = await CreateClientAsync(
                             receiver.Endpoint,
                             stderr,
                             defaultTodoCalendarName: privateCalendarName))
            {
                var query = await client.CallToolAsync(
                    "calendar_occurrences.query",
                    OccurrenceQueryArguments(),
                    cancellationToken: TestContext.Current.CancellationToken);
                query.IsError.ShouldBe(false, query.StructuredContent?.ToString());
                nextCursor = query.StructuredContent!.Value.GetProperty("pagination")
                    .GetProperty("nextCursor")
                    .GetString()
                    .ShouldNotBeNull();

                var created = await client.CallToolAsync(
                    "events.create",
                    CreateEventArguments(createdUid, privateSummary),
                    cancellationToken: TestContext.Current.CancellationToken);
                created.IsError.ShouldBe(false, created.StructuredContent?.ToString());
                var revision = created.StructuredContent!.Value.GetProperty("snapshot")
                    .GetProperty("resourceRevision");
                createdHref = revision.GetProperty("href").GetString();
                createdEntityTag = revision.GetProperty("entityTag").GetString();
            }

            await receiver.WaitForPathsAsync(
                ["/v1/traces", "/v1/metrics", "/v1/logs"],
                TestContext.Current.CancellationToken);

            AssertTraceWaterfalls(receiver.Requests);
            AssertLogsAndMetrics(receiver.Requests);
            AssertProhibitedDataAbsent(
                receiver.Requests,
                recurringUid,
                createdUid,
                privateSummary,
                privateCalendarName,
                recurringHref,
                createdHref!,
                createdEntityTag!,
                nextCursor);
            stderr.ShouldBeEmpty();
        }
        finally
        {
            if (createdHref is not null)
                await DeleteResourceAsync(createdHref, createdEntityTag);
            await DeleteResourceAsync(recurringHref, entityTag: null);
        }
    }

    [Fact]
    public async Task InvalidClientDimensionsTraceStateAndPayloadMarkersAreRedacted()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var privateMethod = $"private-exception-message-{suffix}";
        var privateTool = $"private-tool-url-uid-{suffix}";
        var privateCalendarName = $"private-calendar-name-{suffix}";
        var privateCursor = $"private-cursor-{suffix}";
        var privateMrtrHandle = $"private-mrtr-handle-{suffix}";
        var privateTraceState = $"privatevendor=private-trace-state-{suffix}";
        var reviewedUid = $"private-reviewed-mrtr-{suffix}";
        var reviewedHref = await PutResourceAsync(
            _fixture.TodoCalendarHref,
            $"{reviewedUid}.ics",
            PrivateTodo(reviewedUid));
        var reviewedEntityTag = await GetEntityTagAsync(reviewedHref);
        await using var receiver = OtlpLoopbackReceiver.Start();
        using var process = CreateRawTelemetryProcess(receiver.Endpoint);
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        try
        {
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"privacy-test\",\"version\":\"1\"}}}");
            _ = await ReadRawResponseAsync(process, 1);
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await WriteRawAsync(process, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = privateMethod,
                @params = new
                {
                    _meta = new Dictionary<string, object?>
                    {
                        ["traceparent"] = "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01",
                        ["tracestate"] = privateTraceState
                    }
                }
            }));
            _ = await ReadRawResponseAsync(process, 2);
            await WriteRawAsync(process, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    _meta = new Dictionary<string, object?>
                    {
                        ["traceparent"] = "00-1123456789abcdef0123456789abcdef-1123456789abcdef-01",
                        ["tracestate"] = privateTraceState
                    },
                    name = privateTool,
                    arguments = new
                    {
                        scope = new { mode = "selected", calendar = new { by = "name", name = privateCalendarName } },
                        cursor = privateCursor
                    },
                    requestState = privateMrtrHandle
                }
            }));
            _ = await ReadRawResponseAsync(process, 3);
            await WriteRawAsync(process, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new
                {
                    _meta = new Dictionary<string, object?>
                    {
                        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                        ["io.modelcontextprotocol/clientInfo"] = new { name = "privacy-test", version = "1" },
                        ["io.modelcontextprotocol/clientCapabilities"] = new { }
                    },
                    name = "calendar_resources.delete",
                    arguments = new
                    {
                        revision = new
                        {
                            href = reviewedHref,
                            entityUid = reviewedUid,
                            entityKind = "todo",
                            entityTag = reviewedEntityTag
                        }
                    }
                }
            }));
            var inputRequired = (await ReadRawResponseAsync(process, 4)).GetProperty("result");
            inputRequired.GetProperty("inputRequests").TryGetProperty("confirm_delete", out _)
                .ShouldBeTrue();
            var actualMrtrHandle = inputRequired.GetProperty("requestState").GetString()
                .ShouldNotBeNull();
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            await receiver.WaitForPathsAsync(
                ["/v1/traces", "/v1/metrics"],
                TestContext.Current.CancellationToken);
            var privateValues = new[]
            {
                privateMethod,
                privateTool,
                privateCalendarName,
                privateCursor,
                privateMrtrHandle,
                privateTraceState,
                reviewedUid,
                reviewedHref,
                reviewedEntityTag,
                actualMrtrHandle
            };
            privateValues.ShouldAllBe(value => !OtlpProtobufReader.ContainsUtf8(receiver.Requests, value));
            OtlpProtobufReader.ReadSpans(receiver.Requests)
                .ShouldAllBe(span => span.TraceState.Length == 0);
            var operationMetrics = OtlpProtobufReader.ReadMetrics(receiver.Requests)
                .Where(metric => metric.Name == "mcp.server.operation.duration")
                .ToArray();
            operationMetrics.ShouldNotBeEmpty();
            operationMetrics.SelectMany(metric => metric.DataPointAttributes)
                .SelectMany(attributes => attributes.Keys)
                .ShouldAllBe(key => key == "rpc.response.status_code");
            (await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
            (await stderrTask).ShouldBeEmpty();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await DeleteResourceAsync(reviewedHref, reviewedEntityTag);
        }
    }

    [Fact]
    public async Task TransientReadFailure_ExportsDistinctSafeHttpAttempts()
    {
        await using var server = new RetryingDiscoveryServer();
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        await using (var client = await CreateClientAsync(
                         receiver.Endpoint,
                         stderr,
                         baseUrl: server.BaseUrl,
                         calendarHrefs: server.CalendarHref))
        {
            var result = await client.CallToolAsync(
                "calendars.list",
                cancellationToken: TestContext.Current.CancellationToken);
            result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
        }

        await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
        var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
        var discoveryPhases = spans.Where(span => span.Name == "caldav.phase.discovery").ToArray();
        var attempts = spans.Where(span =>
            span.ScopeName == "DotnetAgents.CalDav.Http"
            && Equals(span.Attributes.GetValueOrDefault("http.request.method"), "PROPFIND"))
            .ToArray();
        attempts.Length.ShouldBeGreaterThanOrEqualTo(2, string.Join(
            Environment.NewLine,
            spans.Select(span => $"{span.ScopeName} | {span.Name} | {string.Join(',', span.Attributes.Select(item => $"{item.Key}={item.Value}"))}")));
        attempts.Select(span => Convert.ToHexString(span.SpanId)).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(attempts.Length);
        attempts.ShouldContain(span =>
            Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 503L));
        attempts.ShouldContain(span =>
            Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 207L));
        attempts.ShouldAllBe(attempt => discoveryPhases.Any(phase =>
            attempt.ParentSpanId.SequenceEqual(phase.SpanId)));
        server.TransientFailureCount.ShouldBe(1);
        spans.ShouldNotContain(span => span.ScopeName == "System.Net.Http");
        spans.ShouldAllBe(span => span.EventCount == 0);
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task AbortedReadAttempt_ExportsControlledFailureWithoutExceptionEvents()
    {
        await using var server = new AbortBeforeHeadersDiscoveryServer();
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        await using (var client = await CreateClientAsync(
                         receiver.Endpoint,
                         stderr,
                         baseUrl: server.BaseUrl,
                         calendarHrefs: server.CalendarHref))
        {
            var result = await client.CallToolAsync(
                "calendars.list",
                cancellationToken: TestContext.Current.CancellationToken);
            result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
        }

        await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
        var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
        var discoveryPhases = spans.Where(span => span.Name == "caldav.phase.discovery").ToArray();
        var attempts = spans.Where(span =>
            span.ScopeName == "DotnetAgents.CalDav.Http"
            && Equals(span.Attributes.GetValueOrDefault("http.request.method"), "PROPFIND"))
            .ToArray();
        attempts.Length.ShouldBeGreaterThanOrEqualTo(2, string.Join(
            Environment.NewLine,
            spans.Select(span => $"{span.ScopeName} | {span.Name} | {string.Join(',', span.Attributes.Select(item => $"{item.Key}={item.Value}"))}")));
        attempts.Select(span => Convert.ToHexString(span.SpanId)).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(attempts.Length);
        attempts.ShouldContain(span =>
            Equals(span.Attributes.GetValueOrDefault("error.type"), "connection_error")
            || Equals(span.Attributes.GetValueOrDefault("error.type"), "response_ended"));
        attempts.ShouldContain(span =>
            Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 207L));
        attempts.ShouldAllBe(attempt => discoveryPhases.Any(phase =>
            attempt.ParentSpanId.SequenceEqual(phase.SpanId)));
        spans.ShouldNotContain(span => span.ScopeName == "System.Net.Http");
        spans.ShouldAllBe(span => span.EventCount == 0);
        server.AbortedRequestCount.ShouldBe(1);
        stderr.ShouldBeEmpty();
    }

    private static void AssertTraceWaterfalls(IReadOnlyList<OtlpRequest> requests)
    {
        var spans = OtlpProtobufReader.ReadSpans(requests);
        var queryMcp = spans.Single(span =>
            span.ScopeName == "Experimental.ModelContextProtocol"
            && span.Name == "tools/call calendar_occurrences.query");
        var queryOperation = spans.Single(span =>
            span.ScopeName == "DotnetAgents.CalDav"
            && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendar_occurrences.query"));
        queryOperation.ParentSpanId.ShouldBe(queryMcp.SpanId);
        spans.ShouldContain(span =>
            span.Name == "caldav.phase.expand"
            && span.ParentSpanId.SequenceEqual(queryOperation.SpanId));

        var createMcp = spans.Single(span =>
            span.ScopeName == "Experimental.ModelContextProtocol"
            && span.Name == "tools/call events.create");
        var createOperation = spans.Single(span =>
            span.ScopeName == "DotnetAgents.CalDav"
            && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "events.create"));
        createOperation.ParentSpanId.ShouldBe(createMcp.SpanId);
        var createPhases = spans.Where(span => span.ParentSpanId.SequenceEqual(createOperation.SpanId)).ToArray();
        createPhases.ShouldContain(span => span.Name == "caldav.phase.reconcile");
        spans.ShouldContain(span =>
            span.ScopeName == "DotnetAgents.CalDav.Http"
            && createPhases.Any(phase => span.ParentSpanId.SequenceEqual(phase.SpanId))
            && span.Attributes.ContainsKey("http.request.method"));
        spans.ShouldAllBe(span => span.EventCount == 0);
        spans.Where(span => span.ScopeName == "DotnetAgents.CalDav")
            .ShouldAllBe(span =>
                span.Name == "caldav.operation"
                || span.Name.StartsWith("caldav.phase.", StringComparison.Ordinal));
        spans.SelectMany(span => span.Attributes.Keys).ShouldNotContain("url.full");
        spans.SelectMany(span => span.Attributes.Keys).ShouldNotContain("mcp.resource.uri");
    }

    private static void AssertLogsAndMetrics(IReadOnlyList<OtlpRequest> requests)
    {
        var metrics = OtlpProtobufReader.ReadMetrics(requests);
        metrics.ShouldContain(metric =>
            metric.ScopeName == "Experimental.ModelContextProtocol"
            && metric.Name == "mcp.server.operation.duration"
            && metric.Unit == "s");
        metrics.SelectMany(metric => metric.DataPointAttributes)
            .SelectMany(attributes => attributes)
            .ShouldAllBe(attribute => IsSafeMetricAttribute(attribute));
        var spans = OtlpProtobufReader.ReadSpans(requests);
        var logs = OtlpProtobufReader.ReadLogs(requests);
        logs.ShouldContain(log =>
            Equals(log.Body, "CalDAV diagnostic")
            && log.TraceId.Length == 16
            && log.SpanId.Length == 8
            && spans.Any(span => span.TraceId.SequenceEqual(log.TraceId)));
        logs.SelectMany(log => log.Attributes.Keys)
            .All(key => key is "Category" or "Code" or "MutationState" or "Outcome" or "Phase")
            .ShouldBeTrue();
    }

    private void AssertProhibitedDataAbsent(
        IReadOnlyList<OtlpRequest> requests,
        params string[] privateValues)
    {
        var prohibited = privateValues.Concat([
            _fixture.BaseUrl,
            "caldavtest",
            "caldavtest123",
            "otlp-private-header",
            "BEGIN:VCALENDAR",
            "Authorization",
            "structuredContent",
            "exception.message",
            "exception.stacktrace"
        ]);
        prohibited.ShouldAllBe(value => !OtlpProtobufReader.ContainsUtf8(requests, value));
    }

    private static bool IsSafeMetricAttribute(KeyValuePair<string, object?> attribute) =>
        attribute.Key switch
        {
            "rpc.response.status_code" => attribute.Value is long,
            "mcp.protocol.version" => Equals(attribute.Value, "2026-07-28"),
            "network.protocol.name" => Equals(attribute.Value, "mcp"),
            "network.transport" => Equals(attribute.Value, "pipe"),
            _ => false
        };

    private async Task<McpClient> CreateClientAsync(
        Uri endpoint,
        ConcurrentQueue<string> stderr,
        string? baseUrl = null,
        string? calendarHrefs = null,
        string? defaultTodoCalendarName = null)
    {
        baseUrl ??= _fixture.BaseUrl;
        var eventCalendarHref = $"{baseUrl}{_fixture.EventCalendarHref}";
        var todoCalendarHref = $"{baseUrl}{_fixture.TodoCalendarHref}";
        calendarHrefs ??= $"{eventCalendarHref},{todoCalendarHref}";
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [GetServerAssemblyPath()],
            WorkingDirectory = AppContext.BaseDirectory,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["CALDAV_URL"] = baseUrl,
                ["CALDAV_USERNAME"] = "caldavtest",
                ["CALDAV_PASSWORD"] = "caldavtest123",
                ["CALDAV_CALENDAR_HREFS"] = calendarHrefs,
                ["CALDAV_DEFAULT_TODO_CALENDAR_NAME"] = defaultTodoCalendarName,
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint.GetLeftPart(UriPartial.Authority),
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
                ["OTEL_EXPORTER_OTLP_HEADERS"] = "authorization=Bearer otlp-private-header",
                ["OTEL_SERVICE_NAME"] = "dotnet-agents-caldav-test",
                ["OTEL_SDK_DISABLED"] = "false",
                ["OTEL_BSP_SCHEDULE_DELAY"] = "100",
                ["OTEL_BLRP_SCHEDULE_DELAY"] = "100",
                ["OTEL_METRIC_EXPORT_INTERVAL"] = "100"
            },
            StandardErrorLines = stderr.Enqueue
        });
        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ProtocolVersion = "2026-07-28",
                DiscoverProbeTimeout = TimeSpan.FromSeconds(10)
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private Dictionary<string, object?> OccurrenceQueryArguments() => new()
    {
        ["scope"] = new Dictionary<string, object?>
        {
            ["mode"] = "selected",
            ["calendar"] = new Dictionary<string, object?>
            {
                ["by"] = "href",
                ["href"] = $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}"
            }
        },
        ["from"] = new Dictionary<string, object?>
        {
            ["kind"] = "utcDateTime",
            ["value"] = "2026-08-15T09:00:00Z"
        },
        ["to"] = new Dictionary<string, object?>
        {
            ["kind"] = "utcDateTime",
            ["value"] = "2026-08-18T11:00:00Z"
        },
        ["pageSize"] = 1
    };

    private Dictionary<string, object?> CreateEventArguments(string uid, string summary) => new()
    {
        ["destination"] = new Dictionary<string, object?>
        {
            ["mode"] = "selected",
            ["calendar"] = new Dictionary<string, object?>
            {
                ["by"] = "href",
                ["href"] = $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}"
            }
        },
        ["entity"] = new Dictionary<string, object?>
        {
            ["kind"] = "event",
            ["uid"] = uid,
            ["fields"] = new Dictionary<string, object?>
            {
                ["summary"] = summary,
                ["start"] = new Dictionary<string, object?>
                {
                    ["kind"] = "utcDateTime",
                    ["value"] = "2026-08-21T13:00:00Z"
                },
                ["duration"] = "PT1H"
            }
        }
    };

    private static string RecurringTodo(string uid) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Telemetry Test//EN\r\n"
        + $"BEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260821T120000Z\r\n"
        + "DTSTART:20260815T100000Z\r\nDUE:20260815T103000Z\r\n"
        + "RRULE:FREQ=DAILY;COUNT=3\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private static string PrivateTodo(string uid) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Telemetry Privacy Test//EN\r\n"
        + $"BEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260821T120000Z\r\n"
        + "SUMMARY:Private reviewed MRTR resource\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private async Task<string> PutResourceAsync(string calendarPath, string name, string content)
    {
        var href = $"{_fixture.BaseUrl}{calendarPath}{name}";
        using var client = CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, href)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/calendar")
        };
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return href;
    }

    private static async Task DeleteResourceAsync(string href, string? entityTag)
    {
        using var client = CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, href);
        if (entityTag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NotFound);
    }

    private static async Task<string> GetEntityTagAsync(string href)
    {
        using var client = CreateAuthenticatedClient();
        using var response = await client.GetAsync(href, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag?.Tag
            ?? throw new InvalidDataException("Radicale response did not contain a strong Entity Tag.");
    }

    private static HttpClient CreateAuthenticatedClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("caldavtest:caldavtest123")));
        return client;
    }

    private Process CreateRawTelemetryProcess(Uri endpoint)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = GetServerAssemblyPath(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        var environment = process.StartInfo.Environment;
        environment["CALDAV_URL"] = _fixture.BaseUrl;
        environment["CALDAV_USERNAME"] = "caldavtest";
        environment["CALDAV_PASSWORD"] = "caldavtest123";
        environment["CALDAV_CALENDAR_HREFS"] = $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}";
        environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint.GetLeftPart(UriPartial.Authority);
        environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        environment["OTEL_BSP_SCHEDULE_DELAY"] = "50";
        environment["OTEL_BLRP_SCHEDULE_DELAY"] = "50";
        environment["OTEL_METRIC_EXPORT_INTERVAL"] = "50";
        return process;
    }

    private static async Task WriteRawAsync(Process process, string message)
    {
        await process.StandardInput.WriteLineAsync(message);
        await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadRawResponseAsync(Process process, int expectedId)
    {
        while (await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.TryGetProperty("id", out var id) && id.GetInt32() == expectedId)
                return message.Clone();
        }
        throw new EndOfStreamException($"MCP process ended before response {expectedId}.");
    }

    private sealed class RetryingDiscoveryServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serve;
        private int _transientFailureCount;
        private int _propFindCount;

        internal RetryingDiscoveryServer()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/";
            CalendarHref = $"{BaseUrl}calendars/test/entities/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serve = ServeAsync();
        }

        internal string BaseUrl { get; }

        internal string CalendarHref { get; }

        internal int TransientFailureCount => Volatile.Read(ref _transientFailureCount);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                System.Diagnostics.Debug.Assert(_stopping.IsCancellationRequested);
            }
            _listener.Close();
            _stopping.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync()
                    .WaitAsync(_stopping.Token)
                    .ConfigureAwait(false);
                await RespondAsync(context).ConfigureAwait(false);
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "PROPFIND")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            if (Interlocked.Increment(ref _propFindCount) == 1)
            {
                Interlocked.Increment(ref _transientFailureCount);
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                context.Response.Headers["Retry-After"] = "0";
                context.Response.Close();
                return;
            }

            var body = context.Request.Url!.AbsolutePath == "/"
                ? "<d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>/calendars/test/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                : "<d:response><d:href>/calendars/test/entities/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><d:displayname>Retry Calendar</d:displayname><c:supported-calendar-component-set><c:comp name=\"VEVENT\"/><c:comp name=\"VTODO\"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
                + body
                + "</d:multistatus>";
            var bytes = Encoding.UTF8.GetBytes(xml);
            context.Response.StatusCode = (int)HttpStatusCode.MultiStatus;
            context.Response.ContentType = "application/xml; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
            context.Response.Close();
        }
    }

    private sealed class AbortBeforeHeadersDiscoveryServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serve;
        private int _abortedRequestCount;
        private int _requestCount;

        internal AbortBeforeHeadersDiscoveryServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/";
            CalendarHref = $"{BaseUrl}calendars/test/entities/";
            _serve = ServeAsync();
        }

        internal string BaseUrl { get; }

        internal string CalendarHref { get; }

        internal int AbortedRequestCount => Volatile.Read(ref _abortedRequestCount);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                System.Diagnostics.Debug.Assert(_stopping.IsCancellationRequested);
            }
            _stopping.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stopping.Token)
                    .ConfigureAwait(false);
                await RespondAsync(client).ConfigureAwait(false);
            }
        }

        private async Task RespondAsync(TcpClient client)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(_stopping.Token);
            while (await reader.ReadLineAsync(_stopping.Token) is { Length: > 0 })
            {
            }

            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                Interlocked.Increment(ref _abortedRequestCount);
                client.Client.LingerState = new LingerOption(enable: true, seconds: 0);
                return;
            }

            var path = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ElementAtOrDefault(1);
            var body = path == "/"
                ? "<d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>/calendars/test/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                : "<d:response><d:href>/calendars/test/entities/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><d:displayname>Abort Recovery Calendar</d:displayname><c:supported-calendar-component-set><c:comp name=\"VEVENT\"/><c:comp name=\"VTODO\"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
                + body
                + "</d:multistatus>";
            var bytes = Encoding.UTF8.GetBytes(xml);
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 207 Multi-Status\r\n"
                + "Content-Type: application/xml; charset=utf-8\r\n"
                + $"Content-Length: {bytes.Length}\r\n"
                + "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, _stopping.Token);
            await stream.WriteAsync(bytes, _stopping.Token);
        }
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
}
