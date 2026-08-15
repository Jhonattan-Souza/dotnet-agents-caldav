using System.Text;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarResourceToolsTests
{
    [Fact]
    public async Task GetAsync_ReturnsFrozenSnapshotShapeWithoutLeakingContentToText()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/a.ics";
        const string content = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:u1\r\nSUMMARY:Secret summary\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var bytes = Encoding.UTF8.GetBytes(content);
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", bytes) with
            {
                Snapshot = new CalendarResourceSnapshot(
                    calendarHref,
                    resourceHref,
                    "\"r1\"",
                    bytes,
                    [],
                    new CalendarResourceProjection(CalendarResourceProjectionKind.Event, "u1", "Secret summary"),
                    [])
            });
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync(resourceHref, CancellationToken.None);

        result.IsError.ShouldBe(false);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("snapshot").GetProperty("resourceRevision").GetProperty("entityTag").GetString().ShouldBe("\"r1\"");
        structured.GetProperty("snapshot").GetProperty("authoritativePayload").GetProperty("base64Utf8").GetString()
            .ShouldBe(Convert.ToBase64String(bytes));
        structured.GetProperty("snapshot").GetProperty("projection").GetProperty("kind").GetString().ShouldBe("event");
        structured.GetProperty("snapshot").GetProperty("entityRevision").GetProperty("entityUid").GetString().ShouldBe("u1");
        result.Content.ShouldHaveSingleItem();
        result.Content[0].ShouldBeOfType<TextContentBlock>().Text.ShouldNotContain("Secret summary");
        result.Content[0].ShouldBeOfType<TextContentBlock>().Text.ShouldNotContain(Convert.ToBase64String(bytes));
    }

    [Fact]
    public async Task GetAsync_MapsTodoPropertiesAndEveryResourceDiagnosticSeverity()
    {
        const string resourceHref = "https://cal.example/tasks/a.ics";
        var bytes = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        var properties = new[]
        {
            new CalendarProperty(
                [new CalendarComponentPathSegment("VCALENDAR", 0), new CalendarComponentPathSegment("VTODO", 0)],
                "DTSTAMP",
                [],
                CalendarPropertyValueType.DateTime,
                "20260815T120000Z",
                "DTSTAMP:20260815T120000Z\r\n")
        };
        var diagnostics = new[]
        {
            new CalendarResourceDiagnostic("info", "safe", CalendarResourceDiagnosticSeverity.Info),
            new CalendarResourceDiagnostic("warning", "safe", CalendarResourceDiagnosticSeverity.Warning),
            new CalendarResourceDiagnostic("error", "safe", CalendarResourceDiagnosticSeverity.Error)
        };
        var snapshot = new CalendarResourceSnapshot(
            "https://cal.example/tasks/",
            resourceHref,
            "\"r1\"",
            bytes,
            properties,
            new CalendarResourceProjection(CalendarResourceProjectionKind.Todo, "u1", null),
            diagnostics);
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, snapshot.EntityTag, bytes) with { Snapshot = snapshot });
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync(resourceHref, CancellationToken.None);

        var serializedSnapshot = result.StructuredContent!.Value.GetProperty("snapshot");
        serializedSnapshot.GetProperty("projection").GetProperty("kind").GetString().ShouldBe("todo");
        serializedSnapshot.GetProperty("entityRevision").GetProperty("entityKind").GetString().ShouldBe("todo");
        serializedSnapshot.GetProperty("calendarProperties")[0].GetProperty("valueType").GetString().ShouldBe("date-time");
        serializedSnapshot.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("severity").GetString())
            .ShouldBe(["info", "warning", "error"]);
    }

    [Fact]
    public async Task GetAsync_MapsUpstreamCancellationToRetryableUnavailable()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw new OperationCanceledException());
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_unavailable");
        result.StructuredContent.Value.GetProperty("retryable").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GetAsync_RejectsStructuredSnapshotLargerThanFourMiB()
    {
        var payload = new byte[3 * 1024 * 1024];
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success("https://cal.example/events/a.ics", "\"r1\"", payload) with
            {
                Snapshot = new CalendarResourceSnapshot(
                    "https://cal.example/events/",
                    "https://cal.example/events/a.ics",
                    "\"r1\"",
                    payload,
                    [],
                    new CalendarResourceProjection(CalendarResourceProjectionKind.Opaque, null, null),
                    [])
            });
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.GetProperty("limits").GetProperty("byteCount").GetInt32().ShouldBeGreaterThan(4 * 1024 * 1024);
    }

    [Fact]
    public async Task GetAsync_ReportsObservedTransportOverflowWithoutPartialSnapshot()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.PayloadTooLarge, ObservedByteCount: (4 * 1024 * 1024) + 1));
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.GetProperty("limits").GetProperty("byteCount").GetInt32().ShouldBe((4 * 1024 * 1024) + 1);
        result.StructuredContent.Value.TryGetProperty("snapshot", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_MapsDiscoveryXmlFailureToSafeProtocolError()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw new XmlException("unsafe upstream text"));
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_protocol_error");
        result.StructuredContent.Value.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("selectionDiscoveryCapability");
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldNotContain("unsafe upstream text");
    }

    [Fact]
    public async Task GetAsync_MapsDiscoveryLimitWithoutSnapshot()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw new CalendarDiscoveryLimitException(257));
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        result.StructuredContent.Value.GetProperty("limits").GetProperty("calendarCount").GetInt32().ShouldBe(257);
        result.StructuredContent.Value.TryGetProperty("snapshot", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_MapsDiscoveryProtocolFailureToSafeProtocolError()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw new CalendarDiscoveryProtocolException("unsafe href"));
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_protocol_error");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("selectionDiscoveryCapability");
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldNotContain("unsafe href");
    }

    [Fact]
    public async Task GetAsync_MapsExceptionalDiscoveryNotFoundToProtocolError()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CalendarResourceRead>(new HttpRequestException("discovery", null, System.Net.HttpStatusCode.NotFound)));
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_protocol_error");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("selectionDiscoveryCapability");
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.InvalidInput, "invalid_input")]
    [InlineData(CalendarResourceReadCode.OutsideScope, "outside_scope")]
    [InlineData(CalendarResourceReadCode.NotFound, "not_found")]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable, "concurrency_unavailable")]
    [InlineData(CalendarResourceReadCode.PayloadTooLarge, "payload_too_large")]
    [InlineData(CalendarResourceReadCode.UpstreamProtocolError, "upstream_protocol_error")]
    public async Task GetAsync_MapsEveryTypedReadFailure(CalendarResourceReadCode readCode, string expectedCode)
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new CalendarResourceRead(readCode));
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, "upstream_unauthorized", false)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "upstream_forbidden", false)]
    [InlineData(System.Net.HttpStatusCode.RequestEntityTooLarge, "payload_too_large", false)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, "upstream_rate_limited", true)]
    [InlineData(System.Net.HttpStatusCode.MethodNotAllowed, "unsupported_capability", false)]
    [InlineData(System.Net.HttpStatusCode.NotImplemented, "unsupported_capability", false)]
    [InlineData(System.Net.HttpStatusCode.InsufficientStorage, "upstream_unavailable", false)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, "upstream_unavailable", true)]
    [InlineData(System.Net.HttpStatusCode.BadRequest, "upstream_protocol_error", false)]
    public async Task GetAsync_MapsEveryRelevantHttpFailure(
        System.Net.HttpStatusCode statusCode,
        string expectedCode,
        bool retryable)
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<CalendarResourceRead>(new HttpRequestException("unsafe upstream", null, statusCode)));
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        result.StructuredContent.Value.GetProperty("retryable").GetBoolean().ShouldBe(retryable);
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldNotContain("unsafe upstream");
    }

    [Fact]
    public async Task GetAsync_MapsTimeoutToRetryableUnavailable()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw new TimeoutException());
        var sut = new CalendarResourceTools(service);

        var result = await sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_unavailable");
        result.StructuredContent.Value.GetProperty("retryable").GetBoolean().ShouldBeTrue();
    }
}
