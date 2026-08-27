using System.Text;
using System.Text.Json;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Mcp.Tools;
using Json.Schema;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

[Collection("TelemetryActivityCollection")]
public sealed class CalendarResourceToolsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_HttpFailurePublishesTheStructuredTerminalError(bool exact)
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<CalendarResourceRead>(new HttpRequestException(
                "unsafe upstream",
                null,
                System.Net.HttpStatusCode.Forbidden)));
        var sut = new CalendarResourceTools(service);
        var exactSut = new ExactCalendarResourceTools(service);

        var (result, operation) = await ToolTelemetryTestScope.CaptureAsync(
            exact ? "calendar_resources.exact_get" : "calendar_resources.get",
            () => exact
                ? exactSut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None)
                : sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None));

        operation.ShouldMatchStructuredError(result.StructuredContent!.Value);
        operation.GetTagItem("caldav.error.code").ShouldBe("upstream_forbidden");
    }

    [Theory]
    [InlineData("CONFIRMED", "confirmed")]
    [InlineData("FUTURE", "other")]
    [InlineData("X-FOO", "other")]
    public async Task GetAsync_ProjectsRecognizedAndOtherStatusWithoutLosingTheRawSlice(
        string status,
        string expectedKind)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/status.ics";
        var originalSlice = $"STATUS;X-CASE=keep:{status}\r\n";
        var properties = new[]
        {
            new CalendarProperty(
                [new CalendarComponentPathSegment("VCALENDAR", 0), new CalendarComponentPathSegment("VEVENT", 0)],
                "STATUS",
                [new CalendarParameter("X-CASE", ["keep"])],
                CalendarPropertyValueType.Text,
                status,
                originalSlice)
        };
        var bytes = Encoding.UTF8.GetBytes(
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T120000Z\r\n{originalSlice}END:VEVENT\r\nEND:VCALENDAR\r\n");
        var snapshot = new CalendarResourceSnapshot(
            calendarHref,
            resourceHref,
            "\"r1\"",
            bytes,
            properties,
            new CalendarResourceProjection(CalendarResourceProjectionKind.Event, "event-1", null),
            []);
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, snapshot.EntityTag, bytes) with { Snapshot = snapshot });

        var result = await new CalendarResourceTools(service).GetAsync(resourceHref, CancellationToken.None);

        var serialized = result.StructuredContent!.Value.GetProperty("snapshot");
        var typedStatus = serialized.GetProperty("projection").GetProperty("fields").GetProperty("status");
        typedStatus.GetProperty("kind").GetString().ShouldBe(expectedKind);
        typedStatus.GetProperty("rawValue").GetString().ShouldBe(status);
        serialized.GetProperty("calendarProperties")[0].GetProperty("originalSlice").GetString()
            .ShouldBe(originalSlice);
    }

    [Fact]
    public async Task GetAsync_ProjectsEveryEventFieldRecurrenceAndStructuredLayerFromAuthoritativeContent()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/rich.ics";
        const string content = "BEGIN:VCALENDAR\r\n"
            + "VERSION:2.0\r\nPRODID:-//Projection Test//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:rich-event\r\nDTSTAMP:20260816T120000Z\r\n"
            + "SUMMARY:Planning\\, review\r\nDESCRIPTION:Line one\\nLine two\r\n"
            + "DTSTART:20260817T130000Z\r\nDTEND:20260817T140000Z\r\nLOCATION:Room 2\r\n"
            + "GEO:12.5;-45.25\r\nSTATUS:FUTURE\r\nTRANSP:OPAQUE\r\nCLASS:X-SECRET\r\n"
            + "PRIORITY:4\r\nCATEGORIES:alpha,beta\\,gamma\r\nURL:https://example.com/event\r\n"
            + "RRULE:FREQ=DAILY;COUNT=3\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\n"
            + "RDATE:20260820T130000Z\r\nRDATE;VALUE=PERIOD:20260821T130000Z/PT1H\r\nEXDATE:20260819T130000Z\r\n"
            + "ORGANIZER;CN=Owner:mailto:owner@example.com\r\n"
            + "ATTENDEE;CN=Guest;ROLE=REQ-PARTICIPANT:mailto:guest@example.com\r\n"
            + "ATTENDEE:mailto:participant@example.com\r\n"
            + "ATTENDEE;ROLE=X-OBSERVER;PARTSTAT=X-WAIT;CUTYPE=X-BOT:urn:uuid:unknown\r\n"
            + "CONTACT;LANGUAGE=en:Help desk\r\nRELATED-TO;RELTYPE=FINISHTOSTART:parent-1\r\n"
            + "REQUEST-STATUS:2.0;Success\r\nATTACH;LABEL=Agenda:https://example.com/agenda\r\n"
            + "BEGIN:PARTICIPANT\r\nUID:participant-1\r\nPARTICIPANT-TYPE:PLANNER-CONTACT\r\n"
            + "CALENDAR-ADDRESS:mailto:participant@example.com\r\nSTATUS:X-READY\r\nSUMMARY:Facilitator\r\nEND:PARTICIPANT\r\n"
            + "BEGIN:VLOCATION\r\nUID:room-123\r\nNAME;LANGUAGE=en:Room\r\nDESCRIPTION:Conference room\r\nGEO:40.2;-8.3\r\n"
            + "LOCATION-TYPE:MEETING-ROOM,ACCESSIBLE\r\nURL:https://example.com/room\r\nEND:VLOCATION\r\n"
            + "BEGIN:VRESOURCE\r\nUID:projector-1\r\nNAME:Projector\r\nRESOURCE-TYPE:PROJECTOR\r\nEND:VRESOURCE\r\n"
            + "BEGIN:VALARM\r\nACTION:X-CUSTOM\r\nTRIGGER:-PT15M\r\nDESCRIPTION:Reminder\r\nUID:alarm-1\r\n"
            + "ACKNOWLEDGED:20260816T100300Z\r\nPROXIMITY:ARRIVE\r\nRELATED-TO;RELTYPE=SNOOZE:alarm-0\r\n"
            + "BEGIN:VLOCATION\r\nUID:door-123\r\nNAME:Door\r\nDESCRIPTION:North entrance\r\nURL:geo:40.1,-8.2\r\nEND:VLOCATION\r\nEND:VALARM\r\n"
            + "END:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:rich-event\r\nDTSTAMP:20260816T120000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260818T130000Z\r\n"
            + "DTSTART:20260818T150000Z\r\nDTEND:20260818T160000Z\r\nSUMMARY:Moved\r\n"
            + "ATTENDEE:mailto:override@example.com\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";
        var bytes = Encoding.UTF8.GetBytes(content);
        var snapshot = CalendarResourceSnapshotFactory.Create(
            calendarHref,
            resourceHref,
            "\"r1\"",
            bytes);
        snapshot.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, snapshot.EntityTag, bytes) with { Snapshot = snapshot });

        var result = await new CalendarResourceTools(service).GetAsync(resourceHref, CancellationToken.None);
        var schema = JsonSchema.FromText(CalendarToolContract.GetOutputSchema("calendar_resources.get").GetRawText());
        var evaluation = schema.Evaluate(
            result.StructuredContent!.Value,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        var projectedProperties = result.StructuredContent.Value.GetProperty("snapshot")
            .GetProperty("calendarProperties").EnumerateArray().Select((property, index) =>
                $"{index}:{property.GetProperty("name").GetString()}:{property.GetProperty("valueType").GetString()}");
        evaluation.IsValid.ShouldBeTrue(
            string.Join(',', projectedProperties) + Environment.NewLine + JsonSerializer.Serialize(evaluation));
        CalendarOutputSchemaGuard.Validate("calendar_resources.get", result);

        var fields = result.StructuredContent!.Value.GetProperty("snapshot").GetProperty("projection").GetProperty("fields");
        fields.GetProperty("summary").GetString().ShouldBe("Planning, review");
        fields.GetProperty("description").GetString().ShouldBe("Line one\nLine two");
        fields.GetProperty("start").GetProperty("kind").GetString().ShouldBe("utcDateTime");
        fields.GetProperty("end").GetProperty("value").GetString().ShouldBe("2026-08-17T14:00:00Z");
        fields.GetProperty("location").GetString().ShouldBe("Room 2");
        fields.GetProperty("geo").GetProperty("longitude").GetDouble().ShouldBe(-45.25);
        fields.GetProperty("status").GetProperty("kind").GetString().ShouldBe("other");
        fields.GetProperty("transparency").GetProperty("kind").GetString().ShouldBe("opaque");
        fields.GetProperty("classification").GetProperty("rawValue").GetString().ShouldBe("X-SECRET");
        fields.GetProperty("priority").GetInt32().ShouldBe(4);
        fields.GetProperty("categories").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(["alpha", "beta,gamma"]);
        fields.GetProperty("url").GetString().ShouldBe("https://example.com/event");
        var recurrence = fields.GetProperty("recurrenceSet");
        recurrence.GetProperty("evaluationState").GetString().ShouldBe("unevaluable");
        recurrence.GetProperty("rrules")[0].GetProperty("text").GetString().ShouldBe("FREQ=DAILY;COUNT=3");
        recurrence.GetProperty("rdates")[0].GetProperty("value").GetString().ShouldBe("2026-08-20T13:00:00Z");
        recurrence.GetProperty("rdates")[1].GetProperty("kind").GetString().ShouldBe("period");
        recurrence.GetProperty("rdates")[1].GetProperty("duration").GetString().ShouldBe("PT1H");
        recurrence.GetProperty("exdates")[0].GetProperty("value").GetString().ShouldBe("2026-08-19T13:00:00Z");
        recurrence.GetProperty("overrides")[0].GetProperty("range").GetString().ShouldBe("this-and-future");
        recurrence.GetProperty("overrides")[0].GetProperty("movedStart").GetProperty("value").GetString()
            .ShouldBe("2026-08-18T15:00:00Z");
        recurrence.GetProperty("overrides")[0].GetProperty("fields").GetProperty("summary").GetString()
            .ShouldBe("Moved");
        recurrence.GetProperty("overrides")[0].GetProperty("fields").GetProperty("structuredData")
            .GetProperty("attendees")[0].GetProperty("uri").GetString().ShouldBe("mailto:override@example.com");
        var structured = fields.GetProperty("structuredData");
        structured.GetProperty("organizer").GetProperty("label").GetString().ShouldBe("Owner");
        var attendees = structured.GetProperty("attendees");
        attendees[0].GetProperty("uri").GetString().ShouldBe("mailto:guest@example.com");
        attendees[1].GetProperty("role").GetProperty("effectiveValue").GetProperty("kind").GetString()
            .ShouldBe("req-participant");
        attendees[1].GetProperty("rsvp").GetProperty("effectiveValue").GetBoolean().ShouldBeFalse();
        attendees[2].GetProperty("role").GetProperty("explicitValue").GetProperty("kind").GetString()
            .ShouldBe("other");
        structured.GetProperty("participants")[0].GetProperty("summary").GetProperty("value").GetString()
            .ShouldBe("Facilitator");
        structured.GetProperty("participants")[0].GetProperty("participantType").GetProperty("kind").GetString()
            .ShouldBe("planner-contact");
        structured.GetProperty("participants")[0].GetProperty("schedulable").GetBoolean().ShouldBeTrue();
        structured.GetProperty("participants")[0].GetProperty("status").GetProperty("kind").GetString()
            .ShouldBe("other");
        structured.GetProperty("contacts")[0].GetProperty("value").GetString().ShouldBe("Help desk");
        structured.GetProperty("relatedTo")[0].GetProperty("relationType").GetProperty("explicitValue")
            .GetProperty("kind").GetString().ShouldBe("finishtostart");
        structured.GetProperty("requestStatuses")[0].GetProperty("code").GetString().ShouldBe("2.0");
        structured.GetProperty("alarms")[0].GetProperty("action").GetProperty("value").GetProperty("kind").GetString()
            .ShouldBe("other");
        structured.GetProperty("alarms")[0].GetProperty("uid").GetProperty("value").GetString()
            .ShouldBe("alarm-1");
        structured.GetProperty("alarms")[0].GetProperty("acknowledged").GetProperty("value")
            .GetProperty("kind").GetString().ShouldBe("utcDateTime");
        structured.GetProperty("alarms")[0].GetProperty("proximity").GetProperty("value")
            .GetProperty("kind").GetString().ShouldBe("arrive");
        structured.GetProperty("alarms")[0].GetProperty("relatedTo")[0].GetProperty("relationType")
            .GetProperty("explicitValue").GetProperty("kind").GetString().ShouldBe("snooze");
        structured.GetProperty("alarms")[0].GetProperty("proximityLocations")[0]
            .GetProperty("uid").GetString().ShouldBe("door-123");
        structured.GetProperty("locationUris")[0].GetProperty("uid").GetString().ShouldBe("room-123");
        structured.GetProperty("locationUris")[0].GetProperty("description").GetProperty("value").GetString()
            .ShouldBe("Conference room");
        structured.GetProperty("locationUris")[0].GetProperty("name").GetProperty("parameters")[0]
            .GetProperty("name").GetString().ShouldBe("LANGUAGE");
        structured.GetProperty("locationUris")[0].GetProperty("componentTypes").GetProperty("value")
            .GetArrayLength().ShouldBe(2);
        structured.GetProperty("resourceUris")[0].GetProperty("componentTypes").GetProperty("value")
            .GetProperty("kind").GetString().ShouldBe("projector");
        structured.GetProperty("attachments")[0].GetProperty("label").GetString().ShouldBe("Agenda");
    }

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
        structured.GetProperty("snapshot").TryGetProperty("authoritativePayload", out _).ShouldBeFalse();
        structured.ToString().ShouldNotContain(Convert.ToBase64String(bytes));
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
        var bytes = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VTODO\r\nUID:u1\r\nDTSTAMP:20260815T120000Z\r\nSTATUS:COMPLETED\r\nCOMPLETED:20260817T140000Z\r\nPERCENT-COMPLETE:100\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
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
        serializedSnapshot.GetProperty("projection").GetProperty("completedAt").GetProperty("value").GetString()
            .ShouldBe("2026-08-17T14:00:00Z");
        serializedSnapshot.GetProperty("projection").GetProperty("fields").GetProperty("percentComplete").GetInt32()
            .ShouldBe(100);
        serializedSnapshot.GetProperty("entityRevision").GetProperty("entityKind").GetString().ShouldBe("todo");
        serializedSnapshot.GetProperty("calendarProperties")[0].GetProperty("valueType").GetString().ShouldBe("date-time");
        serializedSnapshot.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("severity").GetString())
            .ShouldBe(["info", "warning", "error"]);
    }

    [Fact]
    public async Task GetAsync_UncompletedTodoOmitsCompletionInstant()
    {
        const string calendarHref = "https://cal.example/tasks/";
        const string resourceHref = "https://cal.example/tasks/open.ics";
        var bytes = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VTODO\r\nUID:open\r\nDTSTAMP:20260815T120000Z\r\nSTATUS:NEEDS-ACTION\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
        var snapshot = CalendarResourceSnapshotFactory.Create(
            calendarHref,
            resourceHref,
            "\"r1\"",
            bytes);
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, snapshot.EntityTag, bytes) with { Snapshot = snapshot });

        var result = await new CalendarResourceTools(service).GetAsync(resourceHref, CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("snapshot").GetProperty("projection")
            .TryGetProperty("completedAt", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_OpaqueSnapshotOmitsEntityRevisionAndRetainsOneSafeDiagnostic()
    {
        const string resourceHref = "https://cal.example/mixed/a.ics";
        var bytes = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        var snapshot = new CalendarResourceSnapshot(
            "https://cal.example/mixed/",
            resourceHref,
            "\"r1\"",
            bytes,
            [],
            new CalendarResourceProjection(CalendarResourceProjectionKind.Opaque, null, null),
            [new CalendarResourceDiagnostic("mixed_entity_kinds", "The resource is opaque.", CalendarResourceDiagnosticSeverity.Error)]);
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, snapshot.EntityTag, bytes) with { Snapshot = snapshot });

        var result = await new CalendarResourceTools(service).GetAsync(resourceHref, CancellationToken.None);

        var serialized = result.StructuredContent!.Value.GetProperty("snapshot");
        serialized.GetProperty("projection").GetProperty("kind").GetString().ShouldBe("opaque");
        serialized.TryGetProperty("entityRevision", out _).ShouldBeFalse();
        serialized.GetProperty("diagnostics").GetArrayLength().ShouldBe(1);
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
        var payload = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        var largeValue = new string('x', 3 * 1024 * 1024);
        var properties = new[]
        {
            new CalendarProperty(
                [new CalendarComponentPathSegment("VCALENDAR", 0)],
                "X-LARGE",
                [],
                CalendarPropertyValueType.Unknown,
                largeValue,
                $"X-LARGE:{largeValue}\r\n")
        };
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success("https://cal.example/events/a.ics", "\"r1\"", payload) with
            {
                Snapshot = new CalendarResourceSnapshot(
                    "https://cal.example/events/",
                    "https://cal.example/events/a.ics",
                    "\"r1\"",
                    payload,
                    properties,
                    new CalendarResourceProjection(CalendarResourceProjectionKind.Opaque, null, null),
                    [])
            });
        var sut = new CalendarResourceTools(service);

        var (result, operation) = await ToolTelemetryTestScope.CaptureAsync(
            "calendar_resources.get",
            () => sut.GetAsync("https://cal.example/events/a.ics", CancellationToken.None));

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.GetProperty("limits").GetProperty("byteCount").GetInt32().ShouldBeGreaterThan(4 * 1024 * 1024);
        operation.ShouldMatchStructuredError(result.StructuredContent.Value);
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
