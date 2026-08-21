using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
            await using (var client = await CreateClientAsync(receiver.Endpoint, stderr))
            {
                var query = await client.CallToolAsync(
                    "calendar_occurrences.query",
                    OccurrenceQueryArguments(),
                    cancellationToken: TestContext.Current.CancellationToken);
                query.IsError.ShouldBe(false, query.StructuredContent?.ToString());

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
                recurringHref,
                createdHref!);
            stderr.ShouldBeEmpty();
        }
        finally
        {
            if (createdHref is not null)
                await DeleteResourceAsync(createdHref, createdEntityTag);
            await DeleteResourceAsync(recurringHref, entityTag: null);
        }
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
            span.ScopeName == "System.Net.Http"
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
        OtlpProtobufReader.ReadMetrics(requests).ShouldContain(metric =>
            metric.ScopeName == "Experimental.ModelContextProtocol"
            && metric.Name == "mcp.server.operation.duration"
            && metric.Unit == "s");
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

    private async Task<McpClient> CreateClientAsync(Uri endpoint, ConcurrentQueue<string> stderr)
    {
        var eventCalendarHref = $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}";
        var todoCalendarHref = $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}";
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [GetServerAssemblyPath()],
            WorkingDirectory = AppContext.BaseDirectory,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["CALDAV_URL"] = _fixture.BaseUrl,
                ["CALDAV_USERNAME"] = "caldavtest",
                ["CALDAV_PASSWORD"] = "caldavtest123",
                ["CALDAV_CALENDAR_HREFS"] = $"{eventCalendarHref},{todoCalendarHref}",
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
            ["value"] = "2026-08-16T10:15:00Z"
        },
        ["to"] = new Dictionary<string, object?>
        {
            ["kind"] = "utcDateTime",
            ["value"] = "2026-08-16T10:20:00Z"
        }
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

    private static HttpClient CreateAuthenticatedClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("caldavtest:caldavtest123")));
        return client;
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
