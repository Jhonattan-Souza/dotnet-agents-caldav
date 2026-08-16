using System.Text;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarEntityCreateServiceTests
{
    [Theory]
    [InlineData("protocol", CalendarEntityCreateCode.UpstreamProtocolError)]
    [InlineData("xml", CalendarEntityCreateCode.UpstreamProtocolError)]
    [InlineData("unsupported", CalendarEntityCreateCode.UnsupportedCapability)]
    [InlineData("io", CalendarEntityCreateCode.UpstreamUnavailable)]
    [InlineData("cancel", CalendarEntityCreateCode.UpstreamUnavailable)]
    public async Task CreateEventAsync_MapsExpectedSelectionExceptionsBeforeDispatch(
        string failure,
        CalendarEntityCreateCode expectedCode)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<CalendarDescriptor>>(_ => throw failure switch
            {
                "protocol" => new CalendarDiscoveryProtocolException("secret protocol response"),
                "xml" => new XmlException("secret malformed response"),
                "unsupported" => new CalendarDiscoveryUnsupportedCapabilityException("secret unsupported response"),
                "io" => new IOException("secret transport response"),
                _ => new OperationCanceledException("secret upstream cancellation")
            });
        var sut = CreateService(client, "Events");

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "selection-error",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("relative/events/")]
    [InlineData("https://CAL.EXAMPLE/events/")]
    [InlineData("https://user@cal.example/events/")]
    [InlineData("https://cal.example/events/#fragment")]
    [InlineData("https://cal.example/events/?secret=true")]
    [InlineData("https://cal.example/events%2Fnested/")]
    [InlineData("https://cal.example/events%5Cnested/")]
    [InlineData("https://cal.example/private/%2e%2e/events/")]
    [InlineData("https://cal.example/events\\nested/")]
    [InlineData("https://other.example/events/")]
    public async Task CreateEventAsync_RejectsUnsafeSelectedHrefBeforeAnyNetwork(string calendarHref)
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client);

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Selected(new CalendarReference(Href: calendarHref)),
                "unsafe-selected",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_InvalidCompleteCalendarDataFailsAfterSelectionBeforePut()
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        var utcStart = new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z");
        CalendarEventCreateFields[] invalidFields =
        [
            new(Start: utcStart, End: utcStart, Duration: "PT1H"),
            new(Start: utcStart, End: new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-18")),
            new(Start: new CalendarTemporalValue(
                CalendarTemporalKind.ZonedDateTime,
                "2026-03-08T02:30:00",
                "America/New_York")),
            new(Start: new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-17"), Duration: "PT1H"),
            new(Start: utcStart, Description: "line one\rline two"),
            new(Start: utcStart, Summary: "unsafe\u0001control"),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Attachments: [new CalendarNamedUri("relative/path", null, [])])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms: [new CalendarAlarm("display", Trigger("not-a-trigger"), null)])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms: [new CalendarAlarm("email", Trigger("-PT15M"), "Mail reminder")])),
            new(Start: utcStart, Geo: new CalendarGeo(91, 0)),
            new(Start: utcStart, Priority: 10),
            new(Start: utcStart, Status: "not valid"),
            new(Start: utcStart, Status: "REGISTERED-BUT-UNKNOWN"),
            new(Start: utcStart, Transparency: "REGISTERED-BUT-UNKNOWN"),
            new(Start: utcStart, Classification: "REGISTERED-BUT-UNKNOWN"),
            new(Start: utcStart, Url: "relative/event"),
            new(Start: utcStart, Categories: ["safe", "bad\rcategory"]),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                RelatedTo: [new CalendarRelation(string.Empty)])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                RequestStatuses: [new CalendarRequestStatus("2.x", "invalid")])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms: [new CalendarAlarm("display", Trigger("-PT15M"))])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms: [new CalendarAlarm("audio", Trigger("-PT15M"), Repeat: 1)])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms:
                [
                    new CalendarAlarm(
                        "display",
                        Trigger("-PT15M"),
                        "Reminder",
                        Attachments: [new CalendarNamedUri("urn:uuid:forbidden", null, [])])
                ])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms:
                [
                    new CalendarAlarm(
                        "audio",
                        Trigger("-PT15M"),
                        Attachments:
                        [
                            new CalendarNamedUri("urn:uuid:first", null, []),
                            new CalendarNamedUri("urn:uuid:second", null, [])
                        ])
                ])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                StructuredDataUris:
                [
                    new CalendarUriValue(
                        "https://example.test/data",
                        [new CalendarParameter("VALUE", ["TEXT"])])
                ])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Participants: [new CalendarParticipant(string.Empty, "speaker")])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Participants: [new CalendarParticipant("p1", "REGISTERED-BUT-UNKNOWN")])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Participants: [new CalendarParticipant("p1", "speaker", Status: "REGISTERED-BUT-UNKNOWN")])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Participants: [new CalendarParticipant(
                    new CalendarTextValue("p1", [new CalendarParameter("VALUE", ["URI"])]),
                    "speaker")])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Participants: [new CalendarParticipant(
                    "p1",
                    new CalendarTextValue(
                        "speaker",
                        [new CalendarParameter("X-DUP", ["one"]), new CalendarParameter("x-dup", ["two"])]))])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Participants: [new CalendarParticipant(
                    "p1",
                    "speaker",
                    Created: new CalendarTemporalProperty(
                        new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-16T10:00:00Z"),
                        [new CalendarParameter("VALUE", ["DATE"])]))])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Participants: [new CalendarParticipant(
                    "p1",
                    "speaker",
                    Categories: new CalendarTextListProperty(
                        ["one"],
                        [new CalendarParameter("VALUE", ["INTEGER"])]))])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Attendees: [new CalendarAttendee("urn:uuid:guest", [], Role: "REGISTERED-BUT-UNKNOWN")])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Attendees: [new CalendarAttendee("urn:uuid:guest", [], ParticipationStatus: "REGISTERED-BUT-UNKNOWN")])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Attendees: [new CalendarAttendee("urn:uuid:guest", [], CalendarUserType: "REGISTERED-BUT-UNKNOWN")])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Attendees: [new CalendarAttendee(
                    "urn:uuid:guest",
                    [new CalendarParameter("CN", ["duplicate first class"])],
                    CommonName: "Guest")])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Comments: [new CalendarTextValue(
                    "duplicate",
                    [new CalendarParameter("X-DUP", ["one"]), new CalendarParameter("x-dup", ["two"])])])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Comments: [new CalendarTextValue(
                    "safe",
                    [new CalendarParameter("X-NOTE", ["bare\rreturn"])])])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Comments: [new CalendarTextValue(
                    "safe",
                    [new CalendarParameter("X-NOTE", ["unsafe\u0000control"])])])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                RelatedTo: [new CalendarRelation("parent", "REGISTERED-BUT-UNKNOWN")])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                RelatedTo: [new CalendarRelation(
                    "parent",
                    "PARENT",
                    [new CalendarParameter("RELTYPE", ["CHILD"])])])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                RequestStatuses: [new CalendarRequestStatus(
                    "2.0",
                    "Success",
                    Parameters: [new CalendarParameter("VALUE", ["URI"])])])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms: [new CalendarAlarm(
                    new CalendarTextValue("display", [new CalendarParameter("VALUE", ["URI"])]),
                    Trigger("-PT15M"),
                    "Reminder")])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms: [new CalendarAlarm(
                    "display",
                    Trigger("-PT15M"),
                    new CalendarTextValue("Reminder", [new CalendarParameter("VALUE", ["URI"])]))])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms: [new CalendarAlarm(
                    "audio",
                    Trigger("20260817T120000Z"))])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms: [new CalendarAlarm(
                    "audio",
                    Trigger("-PT15M", new CalendarParameter("VALUE", ["DATE-TIME"])))])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Organizer: new CalendarNamedUri(
                    "urn:uuid:owner",
                    null,
                    [new CalendarParameter("bad name", ["value"])]))),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Comments: [new CalendarTextValue(
                    "duplicate value",
                    [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("value", ["TEXT"])])])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Concepts: [new CalendarUriValue("relative/concept", [])])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms: [new CalendarAlarm(
                    "display",
                    Trigger("-PT15M"),
                    "Reminder",
                    Repeat: new CalendarIntegerProperty(0, []),
                    Duration: new CalendarDurationProperty("PT5M", []))])),
            new(Start: utcStart, StructuredData: new CalendarStructuredData(
                Alarms: [new CalendarAlarm(
                    "display",
                    Trigger("-PT15M"),
                    "Reminder",
                    Repeat: new CalendarIntegerProperty(-1, []),
                    Duration: new CalendarDurationProperty("PT5M", []))]))
        ];

        foreach (var fields in invalidFields)
        {
            var result = await sut.CreateEventAsync(
                new CalendarEventCreateRequest(CalendarCreateDestination.Default, "invalid-event", fields),
                CancellationToken.None);
            result.Code.ShouldBe(CalendarEntityCreateCode.InvalidCalendarData);
            result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        }

        await client.Received(invalidFields.Length).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_MissingStartIsCompleteSemanticFailureAfterSelectionBeforePut()
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "missing-start",
                new CalendarEventCreateFields(Summary: "Missing DTSTART")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.InvalidCalendarData);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_CapabilityFailurePrecedesMissingStartSemanticFailure()
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            EventCalendar(calendarHref, "Events") with { EventSupport = EntityKindSupport.NotAdvertised }
        ]);

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "missing-start",
                new CalendarEventCreateFields()),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.UnsupportedCapability);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTodoAsync_InvalidTemporalRelationshipsFailAfterSelectionBeforePut()
    {
        const string calendarHref = "https://cal.example/todos/";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateTodoService(client);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref, "Todos")]);
        var start = new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z");
        CalendarTodoCreateFields[] invalidFields =
        [
            new(Start: start, Due: new CalendarTemporalValue(
                CalendarTemporalKind.UtcDateTime,
                "2026-08-17T14:00:00Z"), Duration: "PT1H"),
            new(Duration: "PT1H"),
            new(Start: start, Due: start),
            new(Start: start, Duration: "-PT1H")
        ];

        foreach (var fields in invalidFields)
        {
            var result = await sut.CreateTodoAsync(
                new CalendarTodoCreateRequest(CalendarCreateDestination.Default, "invalid-todo", fields),
                CancellationToken.None);
            result.Code.ShouldBe(CalendarEntityCreateCode.InvalidCalendarData);
            result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        }

        await client.Received(invalidFields.Length).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_OversizedSerializedResourceFailsAfterSelectionBeforePut()
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "oversized-event",
                new CalendarEventCreateFields(
                    Description: new string('x', (4 * 1024 * 1024) + 1),
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.PayloadTooLarge);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.Dispatched, CalendarEntityCreateCode.CommittedButConcurrencyUnavailable, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, CalendarEntityCreateCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task CreateEventAsync_RefetchWithoutBytesOrStrongEntityTagPreservesMutationTruth(
        CalendarResourceCreateCode transportCode,
        CalendarEntityCreateCode expectedCode,
        CalendarMutationState expectedMutationState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/no-strong-etag.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(transportCode, resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceRead(CalendarResourceReadCode.ConcurrencyUnavailable));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "no-strong-etag",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedMutationState);
        result.Snapshot.ShouldBeNull();
    }

    [Fact]
    public async Task CreateEventAsync_RefetchedLexicalNormalizationRemainsSemanticallyFaithful()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/normalized-event.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        CalendarResourceCreateRequest? dispatched = null;
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            CalendarResourceRead.Success(
                resourceHref,
                "\"normalized-r1\"",
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(dispatched!.AuthoritativeUtf8.Span)
                    .Replace("STATUS:CONFIRMED", "status:confirmed", StringComparison.Ordinal)
                    .Replace("URL:https://example.test/event", "URL;VALUE=URI:https://example.test/event", StringComparison.Ordinal)
                    .Replace("RELATED=end", "RELATED=END", StringComparison.Ordinal)
                    .Replace("PARTICIPANT-TYPE:SPEAKER", "PARTICIPANT-TYPE:speaker", StringComparison.Ordinal)
                    .Replace("ATTENDEE;CN=Guest;X-KEEP=One", "ATTENDEE;X-KEEP=One;CN=Guest", StringComparison.Ordinal))));
        var structured = new CalendarStructuredData(
            Attendees:
            [
                new CalendarAttendee(
                    "mailto:guest@example.test",
                    [new CalendarParameter("X-KEEP", ["One"])],
                    CommonName: "Guest")
            ],
            Participants: [new CalendarParticipant("speaker-id", "speaker")],
            Alarms:
            [
                new CalendarAlarm(
                    "display",
                    Trigger("-PT15M", new CalendarParameter("RELATED", ["end"])),
                    "Reminder")
            ]);

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "normalized-event",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"),
                    Status: "CONFIRMED",
                    Url: "https://example.test/event",
                    StructuredData: structured)),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        result.Snapshot!.EntityTag.ShouldBe("\"normalized-r1\"");
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.Dispatched, CalendarEntityCreateCode.FidelityFailure, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, CalendarEntityCreateCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task CreateEventAsync_RefetchedRequestedFieldMismatchPreservesDispatchTruth(
        CalendarResourceCreateCode transportCode,
        CalendarEntityCreateCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/fidelity-event.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        CalendarResourceCreateRequest? dispatched = null;
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(transportCode, resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            CalendarResourceRead.Success(
                resourceHref,
                "\"changed-r1\"",
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(dispatched!.AuthoritativeUtf8.Span)
                    .Replace("DESCRIPTION:Requested", "DESCRIPTION:Server changed", StringComparison.Ordinal))));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "fidelity-event",
                new CalendarEventCreateFields(
                    Summary: "Same summary",
                    Description: "Requested",
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        result.Snapshot!.EntityTag.ShouldBe("\"changed-r1\"");
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_StoresCompleteFirstClassAndStructuredDataAsInertCalendarContent()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/rich-event.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        CalendarResourceCreateRequest? dispatched = null;
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            CalendarResourceRead.Success(resourceHref, "\"rich-r1\"", dispatched!.AuthoritativeUtf8));
        var structured = new CalendarStructuredData(
            Organizer: new CalendarNamedUri(
                "urn:uuid:organizer",
                "Owner",
                [new CalendarParameter("X-KEEP", ["One", "one"])]),
            Attendees: [new CalendarAttendee(
                "urn:uuid:attendee",
                [new CalendarParameter("X-CUSTOM", ["a", "b"])],
                CommonName: "Guest",
                Role: "required",
                ParticipationStatus: "NEEDS-ACTION",
                Rsvp: true)],
            Participants:
            [
                new CalendarParticipant(
                    new CalendarTextValue("speaker-1", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-UID", ["one"])]),
                    new CalendarTextValue("speaker", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-TYPE", ["one"])]),
                    CalendarAddress: new CalendarUriValue(
                        "urn:uuid:speaker",
                        [new CalendarParameter("VALUE", ["URI"]), new CalendarParameter("X-PART", ["one"])]),
                    Created: new CalendarTemporalProperty(
                        new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-16T10:00:00Z"),
                        [new CalendarParameter("VALUE", ["DATE-TIME"]), new CalendarParameter("X-CREATED", ["one"])]),
                    Description: new CalendarTextValue(
                        "Biography",
                        [new CalendarParameter("LANGUAGE", ["pt-BR"])]),
                    Timestamp: new CalendarTemporalProperty(
                        new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-16T10:01:00Z"),
                        [new CalendarParameter("X-DTSTAMP", ["one"])]),
                    Geo: new CalendarGeoProperty(
                        new CalendarGeo(40.1, -8.2),
                        [new CalendarParameter("VALUE", ["FLOAT"]), new CalendarParameter("X-GEO", ["one"])]),
                    LastModified: new CalendarTemporalProperty(
                        new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-16T10:02:00Z"),
                        [new CalendarParameter("X-MODIFIED", ["one"])]),
                    Priority: new CalendarIntegerProperty(5, [new CalendarParameter("VALUE", ["INTEGER"]), new CalendarParameter("X-PRIORITY", ["one"])]),
                    Sequence: new CalendarIntegerProperty(2, [new CalendarParameter("VALUE", ["INTEGER"]), new CalendarParameter("X-SEQUENCE", ["one"])]),
                    Status: new CalendarTextValue("confirmed", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-STATUS", ["one"])]),
                    Summary: new CalendarTextValue(
                        "Speaker",
                        [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("LANGUAGE", ["en"])]),
                    Url: new CalendarUriValue(
                        "https://calendar.example.test/speaker",
                        [new CalendarParameter("VALUE", ["URI"]), new CalendarParameter("X-LINK", ["profile"])]),
                    Categories: new CalendarTextListProperty(
                        ["one", "two"],
                        [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("LANGUAGE", ["en"])]),
                    RequestStatuses:
                    [
                        new CalendarRequestStatus(
                            "2.0",
                            "Success",
                            Parameters: [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("LANGUAGE", ["en"])])
                    ],
                    RelatedTo:
                    [
                        new CalendarRelation(
                            "parent",
                            "PARENT",
                            [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-REL", ["one"])])
                    ],
                    StructuredDataUris:
                    [
                        new CalendarUriValue(
                            "https://calendar.example.test/speaker.vcf",
                            [new CalendarParameter("VALUE", ["URI"]), new CalendarParameter("FMTTYPE", ["text/vcard"])])
                    ])
            ],
            Contacts: [new CalendarTextValue("Desk", [])],
            Resources: [new CalendarTextValue("Room 1", [])],
            RelatedTo:
            [
                new CalendarRelation(
                    "parent-uid",
                    "PARENT",
                    [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-REL", ["root"])])
            ],
            RequestStatuses:
            [
                new CalendarRequestStatus(
                    "2.0",
                    "Success",
                    Parameters: [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("LANGUAGE", ["pt-BR"])])
            ],
            Alarms:
            [
                new CalendarAlarm(
                    new CalendarTextValue("display", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-ACTION", ["one"])]),
                    Trigger("-PT15M", new CalendarParameter("VALUE", ["DURATION"]), new CalendarParameter("RELATED", ["END"])),
                    new CalendarTextValue("Reminder", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("LANGUAGE", ["en"])])),
                new CalendarAlarm(
                    new CalendarTextValue("email", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-ACTION", ["two"])]),
                    Trigger("-PT30M", new CalendarParameter("VALUE", ["DURATION"])),
                    new CalendarTextValue("Body", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("LANGUAGE", ["en"])]),
                    Repeat: new CalendarIntegerProperty(2, [new CalendarParameter("VALUE", ["INTEGER"]), new CalendarParameter("X-REPEAT", ["one"])]),
                    Duration: new CalendarDurationProperty("PT5M", [new CalendarParameter("VALUE", ["DURATION"]), new CalendarParameter("X-DURATION", ["one"])]),
                    Summary: new CalendarTextValue("Subject", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("LANGUAGE", ["en"])]),
                    Attendees: [new CalendarAttendee("mailto:recipient@example.test", [])],
                    Attachments: [new CalendarNamedUri("https://files.example.test/agenda", null, [])])
            ],
            Attachments: [new CalendarNamedUri("https://files.example.test/a,b;c", "Brief", [])],
            Comments:
            [
                new CalendarTextValue(
                    "Preserve\r\nme",
                    [
                        new CalendarParameter("X-NOTE", ["first\nsecond"]),
                        new CalendarParameter("X-TAB", ["one\ttwo"])
                    ])
            ],
            StyledDescriptions: [new CalendarTextValue("<b>Agenda</b>", [new CalendarParameter("FMTTYPE", ["text/html"])])],
            Images: [new CalendarNamedUri("https://images.example.test/one.png", null, [])],
            Conferences: [new CalendarNamedUri("https://meet.example.test/room", null, [])],
            Links: [new CalendarNamedUri("https://links.example.test/info", null, [])],
            Concepts: [new CalendarUriValue("https://example.test/concepts/planning", [new CalendarParameter("VALUE", ["URI"])])],
            StructuredDataUris:
            [
                new CalendarUriValue(
                    "https://calendar.example.test/event.json",
                    [new CalendarParameter("SCHEMA", ["https://schema.org/Event"])])
            ],
            LocationUris: [new CalendarNamedUri("geo:40.0,-8.0", null, [])],
            ResourceUris: [new CalendarNamedUri("urn:uuid:projector", null, [])]);

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "rich-event",
                new CalendarEventCreateFields(
                    Summary: " Planning ",
                    Description: "Agenda\nSecond line",
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"),
                    End: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T14:00:00Z"),
                    Location: "Room 1",
                    Geo: new CalendarGeo(40.0, -8.0),
                    Status: "CONFIRMED",
                    Transparency: "OPAQUE",
                    Classification: "PRIVATE",
                    Priority: 5,
                    Categories: ["Team", "Planning"],
                    Url: "https://calendar.example.test/events/rich-event",
                    StructuredData: structured)),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot!.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        result.Snapshot.Projection.EntityUid.ShouldBe("rich-event");
        result.Snapshot.Diagnostics.ShouldBeEmpty();
        var content = Encoding.UTF8.GetString(dispatched!.AuthoritativeUtf8.ToArray());
        content.ShouldContain("SUMMARY: Planning \r\n");
        content.ShouldContain("DESCRIPTION:Agenda\\nSecond line\r\n");
        content.ShouldContain("COMMENT;X-NOTE=first^nsecond;X-TAB=one\ttwo:Preserve\\nme\r\n");
        content.ShouldContain("ORGANIZER;CN=Owner;X-KEEP=One,one:urn:uuid:organizer\r\n");
        content.ShouldContain("ATTENDEE;CN=Guest;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE;X-CUSTOM=a,b:urn:uuid:attendee\r\n");
        content.ShouldContain("ATTACH;LABEL=Brief:https://files.example.test/a,b;c\r\n");
        content.ShouldContain("BEGIN:VALARM\r\nACTION;VALUE=TEXT;X-ACTION=one:DISPLAY\r\nTRIGGER;VALUE=DURATION;RELATED=END:-PT15M\r\n");
        content.ShouldContain("ACTION;VALUE=TEXT;X-ACTION=two:EMAIL\r\nTRIGGER;VALUE=DURATION:-PT30M\r\nSUMMARY;VALUE=TEXT;LANGUAGE=en:Subject\r\nDESCRIPTION;VALUE=TEXT;LANGUAGE=en:Body\r\n");
        content.ShouldContain("REPEAT;VALUE=INTEGER;X-REPEAT=one:2\r\nDURATION;VALUE=DURATION;X-DURATION=one:PT5M\r\n");
        content.ShouldContain("ATTENDEE:mailto:recipient@example.test\r\n");
        content.ShouldContain("BEGIN:PARTICIPANT\r\nPARTICIPANT-TYPE;VALUE=TEXT;X-TYPE=one:SPEAKER\r\nUID;VALUE=TEXT;X-UID=one:speaker-1\r\n");
        content.ShouldContain("CREATED;VALUE=DATE-TIME;X-CREATED=one:20260816T100000Z\r\n");
        content.ShouldContain("DTSTAMP;X-DTSTAMP=one:20260816T100100Z\r\n");
        content.ShouldContain("GEO;VALUE=FLOAT;X-GEO=one:40.1;-8.2\r\n");
        content.ShouldContain("LAST-MODIFIED;X-MODIFIED=one:20260816T100200Z\r\n");
        content.ShouldContain("PRIORITY;VALUE=INTEGER;X-PRIORITY=one:5\r\nSEQUENCE;VALUE=INTEGER;X-SEQUENCE=one:2\r\n");
        content.ShouldContain("STATUS;VALUE=TEXT;X-STATUS=one:CONFIRMED\r\n");
        content.ShouldContain("CATEGORIES;VALUE=TEXT;LANGUAGE=en:one,two\r\n");
        content.ShouldContain("RELATED-TO;RELTYPE=PARENT;VALUE=TEXT;X-REL=one:parent\r\n");
        content.ShouldContain("REQUEST-STATUS;VALUE=TEXT;LANGUAGE=en:2.0;Success\r\n");
        content.ShouldContain("CALENDAR-ADDRESS;VALUE=URI;X-PART=one:urn:uuid:speaker\r\n");
        content.ShouldContain("DESCRIPTION;LANGUAGE=pt-BR:Biography\r\n");
        content.ShouldContain("SUMMARY;VALUE=TEXT;LANGUAGE=en:Speaker\r\n");
        content.ShouldContain("URL;VALUE=URI;X-LINK=profile:https://calendar.example.test/speaker\r\n");
        content.ShouldContain("STRUCTURED-DATA;VALUE=URI;FMTTYPE=text/vcard:https://calendar.example.test/speaker.vcf\r\nEND:PARTICIPANT\r\n");
        content.ShouldContain("STRUCTURED-DATA;VALUE=URI;SCHEMA=\"https://schema.org/Event\":https://calendar.example.test/event.json\r\n");
        content.ShouldContain("CONCEPT;VALUE=URI:https://example.test/concepts/planning\r\n");
        content.ShouldContain("BEGIN:VLOCATION\r\nUID:geo:40.0\\,-8.0\r\nEND:VLOCATION\r\n");
        content.ShouldContain("BEGIN:VRESOURCE\r\nUID:urn:uuid:projector\r\nEND:VRESOURCE\r\nEND:VEVENT\r\n");
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_CallerUidCollisionReturnsConflictAfterExactlyOnePut()
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(
                CalendarResourceCreateCode.Conflict,
                calendarHref + "caller-collision.ics"));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "caller/collision",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Is<CalendarResourceCreateRequest>(request =>
                request.ResourceHref == calendarHref + "Jy1iRn6xdSYKh5PMPt5LjbTbKuaHONyxW61grp_SScE.ics"
                && Encoding.UTF8.GetString(request.AuthoritativeUtf8.ToArray()).Contains("UID:caller/collision", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("caller/uid", false)]
    [InlineData("caller\\uid", true)]
    public async Task CreateEventAsync_CrossKindUidCollisionBlocksBeforePutEvenWhenCandidateIsOpaque(
        string uid,
        bool opaqueCandidate)
    {
        const string calendarHref = "https://cal.example/shared/";
        const string existingHref = "https://cal.example/shared/imported.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Shared");
        var shared = EventCalendar(calendarHref, "Shared") with
        {
            TodoSupport = EntityKindSupport.Advertised
        };
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([shared]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([existingHref]);
        var encodedUid = uid.Replace("\\", "\\\\", StringComparison.Ordinal);
        var duplicate = opaqueCandidate ? "SUMMARY:one\r\nSUMMARY:two\r\n" : string.Empty;
        var imported = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Imported//EN\r\nBEGIN:VTODO\r\n"
            + $"UID:{encodedUid}\r\nDTSTAMP:20260816T120000Z\r\n{duplicate}END:VTODO\r\nEND:VCALENDAR\r\n");
        client.GetCalendarResourceAsync(existingHref, Arg.Any<CancellationToken>())
            .Returns(CalendarResourceRead.Success(existingHref, "\"r1\"", imported));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                uid,
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.Received(1).QueryCalendarResourceHrefsAsync(
            calendarHref,
            CalendarEntityKind.Event,
            null,
            null,
            Arg.Any<CancellationToken>());
        await client.Received(1).QueryCalendarResourceHrefsAsync(
            calendarHref,
            CalendarEntityKind.Todo,
            null,
            null,
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTodoAsync_CrossKindEventUidCollisionBlocksBeforePut()
    {
        const string calendarHref = "https://cal.example/shared/";
        const string existingHref = "https://cal.example/shared/imported-event.ics";
        const string uid = "todo/caller";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateTodoService(client);
        var shared = TodoCalendar(calendarHref, "Todos") with
        {
            EventSupport = EntityKindSupport.Advertised
        };
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([shared]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([existingHref]);
        client.GetCalendarResourceAsync(existingHref, Arg.Any<CancellationToken>())
            .Returns(CalendarResourceRead.Success(existingHref, "\"r1\"", Event(uid, "Imported")));

        var result = await sut.CreateTodoAsync(
            new CalendarTodoCreateRequest(
                CalendarCreateDestination.Default,
                uid,
                new CalendarTodoCreateFields(Summary: "No overwrite")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.Received(1).QueryCalendarResourceHrefsAsync(
            calendarHref,
            CalendarEntityKind.Todo,
            null,
            null,
            Arg.Any<CancellationToken>());
        await client.Received(1).QueryCalendarResourceHrefsAsync(
            calendarHref,
            CalendarEntityKind.Event,
            null,
            null,
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(EntityKindSupport.NotAdvertised)]
    [InlineData(EntityKindSupport.Unknown)]
    public async Task CreateEventAsync_OppositeKindUidPreflightIsFailClosedRegardlessOfDiscoveryEvidence(
        EntityKindSupport todoSupport)
    {
        const string calendarHref = "https://cal.example/events/";
        const string existingHref = "https://cal.example/events/imported-todo.ics";
        const string uid = "imported-opposite-kind";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EventCalendar(calendarHref, "Events") with { TodoSupport = todoSupport }]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([existingHref]);
        client.GetCalendarResourceAsync(existingHref, Arg.Any<CancellationToken>())
            .Returns(CalendarResourceRead.Success(existingHref, "\"r1\"", Todo(uid, "Imported")));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                uid,
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.Received(1).QueryCalendarResourceHrefsAsync(
            calendarHref,
            CalendarEntityKind.Todo,
            null,
            null,
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_OppositeKindUidQueryFailureIsNotAttemptedAndNeverFallsThroughToPut()
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<string>>(new HttpRequestException("opposite query failed")));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "fail-closed",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.UpstreamUnavailable);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("caller/uid", "nsHSSPA0YMkwhwKMFnwJ9Oi9sx5sZI4m8LbvGvALNGc")]
    [InlineData("caller\\uid", "KGbA5uA0LZp2Zgus89HvS4sSFMxwQ-DYTvrERkPiDNY")]
    public async Task CreateEventAsync_CallerUidTextIsPreservedWhileResourceNameIsOpaque(
        string uid,
        string resourceName)
    {
        const string calendarHref = "https://cal.example/events/";
        var resourceHref = calendarHref + resourceName + ".ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        CalendarResourceCreateRequest? dispatched = null;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            CalendarResourceRead.Success(resourceHref, "\"strong\"", dispatched!.AuthoritativeUtf8));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                uid,
                new CalendarEventCreateFields(
                    Summary: "Opaque target",
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        result.Snapshot!.Projection.EntityUid.ShouldBe(uid);
        dispatched!.ResourceHref.ShouldBe(resourceHref);
        dispatched.ResourceHref.ShouldNotContain("%2F", Case.Insensitive);
        dispatched.ResourceHref.ShouldNotContain("%5C", Case.Insensitive);
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.InvalidInput, CalendarEntityCreateCode.InvalidInput, CalendarMutationState.NotAttempted)]
    [InlineData(CalendarResourceCreateCode.UnsupportedCapability, CalendarEntityCreateCode.UnsupportedCapability, CalendarMutationState.NotCommitted)]
    [InlineData(CalendarResourceCreateCode.PayloadTooLarge, CalendarEntityCreateCode.PayloadTooLarge, CalendarMutationState.NotCommitted)]
    [InlineData(CalendarResourceCreateCode.UpstreamUnauthorized, CalendarEntityCreateCode.UpstreamUnauthorized, CalendarMutationState.NotCommitted)]
    [InlineData(CalendarResourceCreateCode.UpstreamForbidden, CalendarEntityCreateCode.UpstreamForbidden, CalendarMutationState.NotCommitted)]
    [InlineData(CalendarResourceCreateCode.UpstreamRateLimited, CalendarEntityCreateCode.UpstreamRateLimited, CalendarMutationState.NotCommitted)]
    [InlineData(CalendarResourceCreateCode.UpstreamUnavailable, CalendarEntityCreateCode.UpstreamUnavailable, CalendarMutationState.NotCommitted)]
    [InlineData(CalendarResourceCreateCode.UpstreamProtocolError, CalendarEntityCreateCode.UpstreamProtocolError, CalendarMutationState.NotCommitted)]
    public async Task CreateEventAsync_MapsEveryDefiniteTransportFailureWithoutVerification(
        CalendarResourceCreateCode transportCode,
        CalendarEntityCreateCode expectedCode,
        CalendarMutationState expectedMutationState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/transport.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(transportCode, resourceHref));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "transport",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedMutationState);
        await client.DidNotReceive().GetCalendarResourceAsync(
            resourceHref,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_UninspectableExistingUidSetBlocksBeforePut()
    {
        const string calendarHref = "https://cal.example/events/";
        const string existingHref = "https://cal.example/events/existing.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([existingHref]);
        client.GetCalendarResourceAsync(existingHref, Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceRead(CalendarResourceReadCode.ConcurrencyUnavailable));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "new-event",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.ConcurrencyUnavailable);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("matching", CalendarEntityCreateCode.Conflict)]
    [InlineData("opaque", CalendarEntityCreateCode.OpaqueResource)]
    [InlineData("payload", CalendarEntityCreateCode.PayloadTooLarge)]
    [InlineData("protocol", CalendarEntityCreateCode.UpstreamProtocolError)]
    [InlineData("invalid", CalendarEntityCreateCode.UpstreamProtocolError)]
    [InlineData("outside", CalendarEntityCreateCode.UpstreamProtocolError)]
    [InlineData("gone", CalendarEntityCreateCode.UpstreamForbidden)]
    public async Task CreateEventAsync_MapsEveryUidPreflightReadOutcomeBeforeConditionalPut(
        string scenario,
        CalendarEntityCreateCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string existingHref = "https://cal.example/events/existing.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([existingHref]);
        client.GetCalendarResourceAsync(existingHref, Arg.Any<CancellationToken>()).Returns(scenario switch
        {
            "matching" => CalendarResourceRead.Success(existingHref, "\"r1\"", Event("candidate", "Existing")),
            "opaque" => CalendarResourceRead.Success(existingHref, "\"r1\"", Encoding.UTF8.GetBytes("not-calendar")),
            "payload" => new CalendarResourceRead(CalendarResourceReadCode.PayloadTooLarge),
            "protocol" => new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError),
            "invalid" => new CalendarResourceRead(CalendarResourceReadCode.InvalidInput),
            "outside" => new CalendarResourceRead(CalendarResourceReadCode.OutsideScope),
            _ => new CalendarResourceRead(CalendarResourceReadCode.NotFound)
        });
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(
                CalendarResourceCreateCode.UpstreamForbidden,
                calendarHref + "candidate.ics"));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "candidate",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        await client.Received(scenario == "gone" ? 1 : 0).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_SelectedNameNotFoundReturnsScopedCandidatesBeforePut()
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Selected(new CalendarReference(Name: "Missing")),
                "not-found",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.NotFound);
        result.AuthorizedCandidates!.Select(candidate => candidate.Href).ShouldBe([calendarHref]);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_MoreThanFiveThousandUidCandidatesExhaustsLimitWithoutFetchOrPut()
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, 5_001)
                .Select(index => $"{calendarHref}{index}.ics")
                .ToArray());

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "bounded-event",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.LimitExhausted);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Limits!.ResourcesInspected.ShouldBe(5_001);
        await client.DidNotReceive().GetCalendarResourceAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_ThreeGeneratedUidCollisionsReturnConflictWithoutAFourthPut()
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        var identities = Substitute.For<ICalendarEntityIdentityGenerator>();
        identities.CreateUid().Returns("g1", "g2", "g3", "g4");
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>(),
            TimeProvider.System,
            identities);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new CalendarResourceCreateResult(
                CalendarResourceCreateCode.Conflict,
                call.Arg<CalendarResourceCreateRequest>().ResourceHref));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                null,
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.Received(3).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
        identities.Received(3).CreateUid();
    }

    [Fact]
    public async Task CreateEventAsync_PossiblyDispatchedThenAbsentIsIndeterminateAndNeverRetried()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/uncertain.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(CalendarResourceCreateCode.PossiblyDispatched, resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "uncertain",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Indeterminate);
        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTodoAsync_PersistsCompleteTodoAndReturnsTheRefetchedServerRevision()
    {
        const string calendarHref = "https://cal.example/todos/";
        var resourceHref = CalendarResourceCreateProtocol.BuildResourceHref(calendarHref, "caller-todo");
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                DefaultTodoCalendarName = "Todos"
            }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref, "Todos")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Is<CalendarResourceCreateRequest>(request =>
                    request.ResourceHref == resourceHref
                    && Encoding.UTF8.GetString(request.AuthoritativeUtf8.ToArray()).Contains("BEGIN:VTODO\r\n", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"todo-r2\"", Todo("caller-todo", "Buy milk")));

        var result = await sut.CreateTodoAsync(
            new CalendarTodoCreateRequest(
                CalendarCreateDestination.Default,
                "caller-todo",
                new CalendarTodoCreateFields(Summary: "Buy milk")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        result.Snapshot.ShouldNotBeNull();
        result.Snapshot.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Todo);
        result.Snapshot.EntityTag.ShouldBe("\"todo-r2\"");
    }

    [Fact]
    public async Task CreateTodoAsync_StoresCompleteStructuredTodoWithoutSchedulingSideEffects()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string resourceHref = "https://cal.example/todos/rich-todo.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                DefaultTodoCalendarName = "Todos"
            }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref, "Todos")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        CalendarResourceCreateRequest? dispatched = null;
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            CalendarResourceRead.Success(resourceHref, "\"todo-rich-r1\"", dispatched!.AuthoritativeUtf8));
        var structured = new CalendarStructuredData(
            Organizer: new CalendarNamedUri(
                "mailto:owner@example.test",
                "Owner",
                [new CalendarParameter("VALUE", ["CAL-ADDRESS"])]),
            Attendees:
            [
                new CalendarAttendee(
                    "urn:uuid:chair",
                    [new CalendarParameter("VALUE", ["CAL-ADDRESS"])],
                    Role: "chair"),
                new CalendarAttendee("urn:uuid:optional", [], Role: "optional"),
                new CalendarAttendee("urn:uuid:observer", [], Role: "X-OBSERVER",
                    DelegatedTo: ["urn:uuid:delegate"], SentBy: "mailto:sender@example.test")
            ],
            Participants:
            [
                new CalendarParticipant(
                    new CalendarTextValue("todo-speaker", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-UID", ["todo"])]),
                    new CalendarTextValue("speaker", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-TYPE", ["todo"])]),
                    Timestamp: new CalendarTemporalProperty(
                        new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-16T11:00:00Z"),
                        [new CalendarParameter("VALUE", ["DATE-TIME"]), new CalendarParameter("X-DTSTAMP", ["todo"])]),
                    Status: new CalendarTextValue("needs-action", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-STATUS", ["todo"])]),
                    Categories: new CalendarTextListProperty(
                        ["work", "planning"],
                        [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("LANGUAGE", ["en"])]) )
            ],
            RelatedTo:
            [
                new CalendarRelation(
                    "todo-parent",
                    "PARENT",
                    [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-REL", ["todo"])])
            ],
            RequestStatuses:
            [
                new CalendarRequestStatus(
                    "2.0",
                    "Success",
                    Parameters: [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("LANGUAGE", ["en"])])
            ],
            Comments:
            [
                new CalendarTextValue(
                    "Todo\ncomment",
                    [new CalendarParameter("X-NOTE", ["first\r\nsecond"])])
            ],
            Alarms:
            [
                new CalendarAlarm(
                    new CalendarTextValue("audio", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-ACTION", ["audio"])]),
                    Trigger("20260817T120000Z", new CalendarParameter("VALUE", ["DATE-TIME"]))),
                new CalendarAlarm(
                    new CalendarTextValue("display", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("X-ACTION", ["display"])]),
                    Trigger("-PT15M", new CalendarParameter("VALUE", ["DURATION"])),
                    new CalendarTextValue("Reminder", [new CalendarParameter("VALUE", ["TEXT"]), new CalendarParameter("LANGUAGE", ["en"])]),
                    new CalendarIntegerProperty(1, [new CalendarParameter("VALUE", ["INTEGER"]), new CalendarParameter("X-REPEAT", ["todo"])]),
                    new CalendarDurationProperty("PT5M", [new CalendarParameter("VALUE", ["DURATION"]), new CalendarParameter("X-DURATION", ["todo"])]) )
            ],
            Attachments:
            [
                new CalendarNamedUri(
                    "https://files.example.test/todo",
                    null,
                    [new CalendarParameter("VALUE", ["URI"]), new CalendarParameter("X-LIST", ["one,two"])])
            ],
            Concepts:
            [
                new CalendarUriValue(
                    "https://example.test/concepts/todo",
                    [new CalendarParameter("VALUE", ["URI"])])
            ],
            LocationUris: [new CalendarNamedUri("geo:40.0,-8.0", "Office", [])],
            ResourceUris: [new CalendarNamedUri("urn:uuid:projector", "Projector", [])]);

        var result = await sut.CreateTodoAsync(
            new CalendarTodoCreateRequest(
                CalendarCreateDestination.Default,
                "rich-todo",
                new CalendarTodoCreateFields(
                    Summary: "Rich todo",
                    Description: "Stored\r\nonly",
                    Start: new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-17"),
                    Due: new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-18"),
                    Status: "NEEDS-ACTION",
                    Priority: 4,
                    Categories: ["Work", "Planning"],
                    StructuredData: structured)),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot!.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Todo);
        result.Snapshot.Projection.EntityUid.ShouldBe("rich-todo");
        result.Snapshot.Diagnostics.ShouldBeEmpty();
        var content = Encoding.UTF8.GetString(dispatched!.AuthoritativeUtf8.Span);
        content.ShouldContain("BEGIN:VTODO\r\n");
        content.ShouldContain("DESCRIPTION:Stored\\nonly\r\n");
        content.ShouldContain("COMMENT;X-NOTE=first^nsecond:Todo\\ncomment\r\n");
        content.ShouldContain("ROLE=CHAIR");
        content.ShouldContain("ROLE=OPT-PARTICIPANT");
        content.ShouldContain("ROLE=X-OBSERVER");
        content.ShouldContain("DELEGATED-TO=\"urn:uuid:delegate\"");
        content.ShouldContain("SENT-BY=\"mailto:sender@example.test\"");
        content.ShouldContain("ACTION;VALUE=TEXT;X-ACTION=audio:AUDIO\r\nTRIGGER;VALUE=DATE-TIME:20260817T120000Z\r\n");
        content.ShouldContain("PARTICIPANT-TYPE;VALUE=TEXT;X-TYPE=todo:SPEAKER\r\nUID;VALUE=TEXT;X-UID=todo:todo-speaker\r\n");
        content.ShouldContain("DTSTAMP;VALUE=DATE-TIME;X-DTSTAMP=todo:20260816T110000Z\r\n");
        content.ShouldContain("STATUS;VALUE=TEXT;X-STATUS=todo:NEEDS-ACTION\r\nCATEGORIES;VALUE=TEXT;LANGUAGE=en:work,planning\r\n");
        content.ShouldContain("RELATED-TO;RELTYPE=PARENT;VALUE=TEXT;X-REL=todo:todo-parent\r\n");
        content.ShouldContain("REQUEST-STATUS;VALUE=TEXT;LANGUAGE=en:2.0;Success\r\n");
        content.ShouldContain("ACTION;VALUE=TEXT;X-ACTION=display:DISPLAY\r\nTRIGGER;VALUE=DURATION:-PT15M\r\nDESCRIPTION;VALUE=TEXT;LANGUAGE=en:Reminder\r\n");
        content.ShouldContain("REPEAT;VALUE=INTEGER;X-REPEAT=todo:1\r\nDURATION;VALUE=DURATION;X-DURATION=todo:PT5M\r\n");
        content.ShouldContain("CONCEPT;VALUE=URI:https://example.test/concepts/todo\r\n");
        content.ShouldContain("X-LIST=\"one,two\"");
        content.ShouldContain("BEGIN:VLOCATION\r\nUID:geo:40.0\\,-8.0\r\nNAME:Office\r\nEND:VLOCATION\r\n");
        content.ShouldContain("END:VRESOURCE\r\nEND:VTODO\r\n");
    }

    [Fact]
    public async Task CreateTodoAsync_CallerUidFoundDuringPreflightReturnsConflictWithoutPut()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string existingHref = "https://cal.example/todos/existing.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateTodoService(client);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref, "Todos")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([existingHref]);
        client.GetCalendarResourceAsync(existingHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(existingHref, "\"existing-r1\"", Todo("caller-todo", "Already stored")));

        var result = await sut.CreateTodoAsync(
            new CalendarTodoCreateRequest(
                CalendarCreateDestination.Default,
                "caller-todo",
                new CalendarTodoCreateFields(Summary: "New value")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTodoAsync_ThreeGeneratedUidCollisionsUseFreshIdentitiesWithoutAFourthPut()
    {
        const string calendarHref = "https://cal.example/todos/";
        var expectedHrefs = new[] { "todo-1", "todo-2", "todo-3" }
            .Select(uid => CalendarResourceCreateProtocol.BuildResourceHref(calendarHref, uid))
            .ToArray();
        var client = Substitute.For<ICalendarClient>();
        var identities = Substitute.For<ICalendarEntityIdentityGenerator>();
        identities.CreateUid().Returns("todo-1", "todo-2", "todo-3", "todo-4");
        var sut = CreateTodoService(client, identities);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref, "Todos")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new CalendarResourceCreateResult(
                CalendarResourceCreateCode.Conflict,
                call.Arg<CalendarResourceCreateRequest>().ResourceHref));

        var result = await sut.CreateTodoAsync(
            new CalendarTodoCreateRequest(
                CalendarCreateDestination.Default,
                null,
                new CalendarTodoCreateFields(Summary: "Generated identity")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.Received(3).CreateCalendarResourceAsync(
            Arg.Is<CalendarResourceCreateRequest>(request =>
                expectedHrefs.Contains(request.ResourceHref, StringComparer.Ordinal)),
            Arg.Any<CancellationToken>());
        identities.Received(3).CreateUid();
    }

    [Fact]
    public async Task CreateTodoAsync_PossiblyDispatchedEquivalentRichRefetchReturnsCoherentSuccess()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string resourceHref = "https://cal.example/todos/normalized-todo.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateTodoService(client);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref, "Todos")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        CalendarResourceCreateRequest? dispatched = null;
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(CalendarResourceCreateCode.PossiblyDispatched, resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            CalendarResourceRead.Success(
                resourceHref,
                "\"normalized-todo-r1\"",
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(dispatched!.AuthoritativeUtf8.Span)
                    .Replace("STATUS:NEEDS-ACTION", "status:needs-action", StringComparison.Ordinal)
                    .Replace("PRIORITY:4", "PRIORITY:+004", StringComparison.Ordinal)
                    .Replace(
                        "ATTENDEE;CN=Guest;ROLE=REQ-PARTICIPANT;RSVP=TRUE;X-Z=zeta;X-A=alpha:",
                        "attendee;X-A=alpha;RSVP=true;X-Z=zeta;ROLE=req-participant;CN=Guest:",
                        StringComparison.Ordinal))));
        var structured = new CalendarStructuredData(
            Attendees:
            [
                new CalendarAttendee(
                    "urn:uuid:guest",
                    [new CalendarParameter("X-Z", ["zeta"]), new CalendarParameter("X-A", ["alpha"])],
                    CommonName: "Guest",
                    Role: "required",
                    Rsvp: true)
            ],
            Comments: [new CalendarTextValue("First", []), new CalendarTextValue("Second", [])]);

        var result = await sut.CreateTodoAsync(
            new CalendarTodoCreateRequest(
                CalendarCreateDestination.Default,
                "normalized-todo",
                new CalendarTodoCreateFields(
                    Summary: "Lexical normalization",
                    Status: "NEEDS-ACTION",
                    Priority: 4,
                    StructuredData: structured)),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot!.EntityTag.ShouldBe("\"normalized-todo-r1\"");
        result.Snapshot.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Todo);
        result.Snapshot.Projection.EntityUid.ShouldBe("normalized-todo");
        result.Snapshot.Diagnostics.ShouldBeEmpty();
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTodoAsync_RefetchDroppingStructuredMultiplicityReturnsCommittedFidelityFailure()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string resourceHref = "https://cal.example/todos/altered-todo.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateTodoService(client);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref, "Todos")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        CalendarResourceCreateRequest? dispatched = null;
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            CalendarResourceRead.Success(
                resourceHref,
                "\"altered-todo-r1\"",
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(dispatched!.AuthoritativeUtf8.Span)
                    .Replace("COMMENT:Second\r\n", string.Empty, StringComparison.Ordinal))));
        var structured = new CalendarStructuredData(
            Comments: [new CalendarTextValue("First", []), new CalendarTextValue("Second", [])],
            ResourceUris: [new CalendarNamedUri("urn:uuid:projector", "Projector", [])]);

        var result = await sut.CreateTodoAsync(
            new CalendarTodoCreateRequest(
                CalendarCreateDestination.Default,
                "altered-todo",
                new CalendarTodoCreateFields(Summary: "Preserve all values", StructuredData: structured)),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.FidelityFailure);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot!.EntityTag.ShouldBe("\"altered-todo-r1\"");
        Encoding.UTF8.GetString(result.Snapshot.AuthoritativeUtf8.Span).ShouldNotContain("COMMENT:Second\r\n");
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("uid-control")]
    [InlineData("missing-selector")]
    [InlineData("both-selectors")]
    public async Task CreateTodoAsync_InvalidRequestShapeFailsBeforeDiscovery(string scenario)
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateTodoService(client);
        var destination = scenario switch
        {
            "missing-selector" => CalendarCreateDestination.Selected(new CalendarReference()),
            "both-selectors" => CalendarCreateDestination.Selected(new CalendarReference(
                Href: "https://cal.example/todos/",
                Name: "Todos")),
            _ => CalendarCreateDestination.Default
        };
        var uid = scenario == "uid-control" ? "unsafe\nuid" : "safe-uid";

        var result = await sut.CreateTodoAsync(
            new CalendarTodoCreateRequest(destination, uid, new CalendarTodoCreateFields(Summary: "Invalid shape")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTodoAsync_SelectedHrefOutsideConfiguredScopeFailsBeforeDiscovery()
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                CalendarHrefs = "https://cal.example/authorized/",
                DefaultTodoCalendarName = "Todos"
            }),
            Substitute.For<ILogger<CalendarService>>());

        var result = await sut.CreateTodoAsync(
            new CalendarTodoCreateRequest(
                CalendarCreateDestination.Selected(new CalendarReference(
                    Href: "https://cal.example/private/")),
                "outside-scope-todo",
                new CalendarTodoCreateFields(Summary: "Outside scope")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.OutsideScope);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTodoAsync_AmbiguousSelectedNameReturnsOnlyScopedCandidatesWithoutPut()
    {
        const string firstHref = "https://cal.example/todos-one/";
        const string secondHref = "https://cal.example/todos-two/";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                CalendarHrefs = $"{firstHref},{secondHref}"
            }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar(firstHref, "Shared"),
            TodoCalendar(secondHref, "Shared"),
            TodoCalendar("https://cal.example/private/", "Shared")
        ]);

        var result = await sut.CreateTodoAsync(
            new CalendarTodoCreateRequest(
                CalendarCreateDestination.Selected(new CalendarReference(Name: "Shared")),
                "ambiguous-todo",
                new CalendarTodoCreateFields(Summary: "Ambiguous")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Ambiguous);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.AuthorizedCandidates!.Select(candidate => candidate.Href).ShouldBe([firstHref, secondHref]);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTodoAsync_UnadvertisedDestinationCapabilityFailsBeforePut()
    {
        const string calendarHref = "https://cal.example/todos/";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateTodoService(client);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            TodoCalendar(calendarHref, "Todos") with { TodoSupport = EntityKindSupport.NotAdvertised }
        ]);

        var result = await sut.CreateTodoAsync(
            new CalendarTodoCreateRequest(
                CalendarCreateDestination.Default,
                "unsupported-todo",
                new CalendarTodoCreateFields(Summary: "Unsupported")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.UnsupportedCapability);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.AuthorizedCandidates!.Select(candidate => candidate.Href).ShouldBe([calendarHref]);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_RefetchPreservesEscapedTextSemanticsAcrossPropertyTokenCase()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/escaped-event.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        CalendarResourceCreateRequest? dispatched = null;
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            CalendarResourceRead.Success(
                resourceHref,
                "\"escaped-r1\"",
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(dispatched!.AuthoritativeUtf8.Span)
                    .Replace("SUMMARY:", "summary:", StringComparison.Ordinal))));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "escaped-event",
                new CalendarEventCreateFields(
                    Summary: "Path \\server, phase; one",
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        result.Snapshot!.EntityTag.ShouldBe("\"escaped-r1\"");
    }

    [Fact]
    public async Task CreateEventAsync_RefetchWithInvalidGeoLexemeReturnsCommittedFidelityFailure()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/invalid-geo-event.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        CalendarResourceCreateRequest? dispatched = null;
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            CalendarResourceRead.Success(
                resourceHref,
                "\"invalid-geo-r1\"",
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(dispatched!.AuthoritativeUtf8.Span)
                    .Replace("GEO:40;-8", "GEO:invalid;-8", StringComparison.Ordinal))));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "invalid-geo-event",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"),
                    Geo: new CalendarGeo(40, -8))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.FidelityFailure);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot!.EntityTag.ShouldBe("\"invalid-geo-r1\"");
    }

    [Fact]
    public async Task CreateEventAsync_UnknownDestinationCapabilityReturnsUnsupportedBeforePut()
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            EventCalendar(calendarHref, "Events") with { EventSupport = EntityKindSupport.Unknown }
        ]);

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Selected(new CalendarReference(Href: calendarHref)),
                "blocked-event",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.UnsupportedCapability);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_PossiblyDispatchedWriteUsesOnlyGetToReconcileCommittedSemantics()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/ambiguous.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(CalendarResourceCreateCode.PossiblyDispatched, resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", Event("ambiguous", "Ambiguous")));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "ambiguous",
                new CalendarEventCreateFields(
                    Summary: "Ambiguous",
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_PossiblyDispatchedWriteReconcilesAfterCallerCancellation()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/cancelled-after-dispatch.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        using var callerCancellation = new CancellationTokenSource();
        CalendarResourceCreateRequest? dispatched = null;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callerCancellation.Cancel();
                return new CalendarResourceCreateResult(
                    CalendarResourceCreateCode.PossiblyDispatched,
                    resourceHref);
            });
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().IsCancellationRequested.ShouldBeFalse();
            return CalendarResourceRead.Success(
                resourceHref,
                "\"reconciled-r1\"",
                dispatched!.AuthoritativeUtf8);
        });

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "cancelled-after-dispatch",
                new CalendarEventCreateFields(
                    Summary: "Reconcile me",
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            callerCancellation.Token);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        result.Snapshot!.EntityTag.ShouldBe("\"reconciled-r1\"");
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(
            resourceHref,
            Arg.Is<CancellationToken>(token => !token.IsCancellationRequested));
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.Dispatched, CalendarEntityCreateCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, CalendarEntityCreateCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task CreateEventAsync_ReconciliationStopsAfterThirtyAdditionalSecondsWithoutSleeping(
        CalendarResourceCreateCode transportCode,
        CalendarEntityCreateCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/deadline.ics";
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                DefaultEventCalendarName = "Events"
            }),
            Substitute.For<ILogger<CalendarService>>(),
            timeProvider,
            Substitute.For<ICalendarEntityIdentityGenerator>());
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(transportCode, resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            var completion = new TaskCompletionSource<CalendarResourceRead>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            readEntered.TrySetResult();
            return await completion.Task;
        });
        var pending = sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "deadline",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);
        await readEntered.Task;

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_PreDispatchWorkStopsAtThirtySecondsWithoutWriting()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                DefaultEventCalendarName = "Events"
            }),
            Substitute.For<ILogger<CalendarService>>(),
            timeProvider,
            Substitute.For<ICalendarEntityIdentityGenerator>());
        var discoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            var completion = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            discoveryEntered.TrySetResult();
            return await completion.Task;
        });
        var pending = sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "pre-dispatch-deadline",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);
        await discoveryEntered.Task;

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarEntityCreateCode.LimitExhausted);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_DirectPutNotFoundIsNotCommittedWithoutVerification()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/missing-parent.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(CalendarResourceCreateCode.NotFound, resourceHref));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "missing-parent",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.NotFound);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.DidNotReceive().GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.Dispatched, "http", CalendarEntityCreateCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.Dispatched, "timeout", CalendarEntityCreateCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.Dispatched, "io", CalendarEntityCreateCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.Dispatched, "protocol", CalendarEntityCreateCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, "http", CalendarEntityCreateCode.Indeterminate, CalendarMutationState.Unknown)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, "timeout", CalendarEntityCreateCode.Indeterminate, CalendarMutationState.Unknown)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, "io", CalendarEntityCreateCode.Indeterminate, CalendarMutationState.Unknown)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, "protocol", CalendarEntityCreateCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task CreateEventAsync_VerificationReadFailurePreservesMutationTruthWithoutRetry(
        CalendarResourceCreateCode transportCode,
        string failure,
        CalendarEntityCreateCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/read-failure.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(transportCode, resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>())
            .Returns<CalendarResourceRead>(_ => failure switch
            {
                "http" => throw new HttpRequestException("safe verification failure"),
                "timeout" => throw new TimeoutException("safe verification timeout"),
                "io" => throw new IOException("safe verification I/O failure"),
                _ => throw new CalendarDiscoveryProtocolException("safe verification protocol failure")
            });

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "read-failure",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.Dispatched, CalendarEntityCreateCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, CalendarEntityCreateCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task CreateEventAsync_PostWriteNotFoundPreservesDispatchTruth(
        CalendarResourceCreateCode transportCode,
        CalendarEntityCreateCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/missing-after-write.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(transportCode, resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceRead(CalendarResourceReadCode.NotFound));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "missing-after-write",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_CallerCancellationBeforeDispatchPropagatesWithoutWriting()
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client, defaultEventName: "Events");
        using var callerCancellation = new CancellationTokenSource();
        var discoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            var completion = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            discoveryEntered.TrySetResult();
            return await completion.Task;
        });
        var pending = sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "cancel-before-dispatch",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            callerCancellation.Token);
        await discoveryEntered.Task;

        await callerCancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(pending);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.Dispatched, CalendarEntityCreateCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, CalendarEntityCreateCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task CreateEventAsync_ReconciliationHonorsSixtySecondTotalCeilingAfterLateTransportReturn(
        CalendarResourceCreateCode transportCode,
        CalendarEntityCreateCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/late-dispatch.ics";
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                DefaultEventCalendarName = "Events"
            }),
            Substitute.For<ILogger<CalendarService>>(),
            timeProvider,
            Substitute.For<ICalendarEntityIdentityGenerator>());
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                timeProvider.Advance(TimeSpan.FromSeconds(35));
                return new CalendarResourceCreateResult(transportCode, resourceHref);
            });
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            var completion = new TaskCompletionSource<CalendarResourceRead>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            readEntered.TrySetResult();
            return await completion.Task;
        });
        var pending = sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "late-dispatch",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);
        await readEntered.Task;

        timeProvider.Advance(TimeSpan.FromSeconds(25));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.Dispatched, CalendarEntityCreateCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, CalendarEntityCreateCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task CreateEventAsync_TotalDeadlineConsumedAtTransportReturnDoesNotStartAGet(
        CalendarResourceCreateCode transportCode,
        CalendarEntityCreateCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/total-deadline.ics";
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                DefaultEventCalendarName = "Events"
            }),
            Substitute.For<ILogger<CalendarService>>(),
            timeProvider,
            Substitute.For<ICalendarEntityIdentityGenerator>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                timeProvider.Advance(TimeSpan.FromSeconds(60));
                return new CalendarResourceCreateResult(transportCode, resourceHref);
            });

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "total-deadline",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_GeneratedUidCollisionUsesOneFreshIdentityWithinTheThreeAttemptBound()
    {
        const string calendarHref = "https://cal.example/events/";
        var expectedHrefs = new[] { "generated-1", "generated-2" }
            .Select(uid => CalendarResourceCreateProtocol.BuildResourceHref(calendarHref, uid))
            .ToArray();
        var client = Substitute.For<ICalendarClient>();
        var identities = Substitute.For<ICalendarEntityIdentityGenerator>();
        identities.CreateUid().Returns("generated-1", "generated-2");
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>(),
            TimeProvider.System,
            identities);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref, "Events")]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        var requests = new List<CalendarResourceCreateRequest>();
        client.CreateCalendarResourceAsync(
                Arg.Do<CalendarResourceCreateRequest>(requests.Add),
                Arg.Any<CancellationToken>())
            .Returns(
                new CalendarResourceCreateResult(CalendarResourceCreateCode.Conflict, expectedHrefs[0]),
                CalendarResourceCreateResult.Dispatched(expectedHrefs[1]));
        client.GetCalendarResourceAsync(expectedHrefs[1], Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(
                expectedHrefs[1],
                "\"r2\"",
                Event("generated-2", "Generated")));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                null,
                new CalendarEventCreateFields(
                    Summary: "Generated",
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        requests.Select(request => request.ResourceHref).ShouldBe(expectedHrefs);
        requests.Select(request => Encoding.UTF8.GetString(request.AuthoritativeUtf8.ToArray()))
            .ShouldAllBe(content => content.Contains("UID:generated-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateEventAsync_SelectedHrefResolvesInsideScopeWithoutUsingTheDefault()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/selected-event.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            EventCalendar(calendarHref, "Events")
        ]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", Event("selected-event", "Selected")));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Selected(new CalendarReference(Href: calendarHref)),
                "selected-event",
                new CalendarEventCreateFields(
                    Summary: "Selected",
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Is<CalendarResourceCreateRequest>(request => request.CalendarHref == calendarHref),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_SelectedNameAmbiguityReturnsOnlyAuthorizedCandidatesBeforePut()
    {
        const string firstHref = "https://cal.example/first/";
        const string secondHref = "https://cal.example/second/";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                CalendarHrefs = $"{firstHref},{secondHref}"
            }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            EventCalendar(firstHref, "Shared"),
            EventCalendar(secondHref, "Shared"),
            EventCalendar("https://cal.example/private/", "Shared")
        ]);

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Selected(new CalendarReference(Name: "Shared")),
                "ambiguous-selection",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Ambiguous);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.AuthorizedCandidates!.Select(candidate => candidate.Href).ShouldBe([firstHref, secondHref]);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_SelectedHrefOutsideConfiguredScopeFailsBeforeDiscovery()
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                CalendarHrefs = "https://cal.example/authorized/"
            }),
            Substitute.For<ILogger<CalendarService>>());

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Selected(new CalendarReference(
                    Href: "https://cal.example/private/")),
                "outside-scope",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.OutsideScope);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateEventAsync_UsesSelectedCapabilityAndReturnsTheRefetchedServerRevision()
    {
        const string calendarHref = "https://cal.example/events/";
        var resourceHref = CalendarResourceCreateProtocol.BuildResourceHref(calendarHref, "caller-event");
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                DefaultEventCalendarName = "Events"
            }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            }
        ]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.CreateCalendarResourceAsync(
                Arg.Is<CalendarResourceCreateRequest>(request =>
                    request.CalendarHref == calendarHref
                    && request.ResourceHref == resourceHref
                    && Encoding.UTF8.GetString(request.AuthoritativeUtf8.ToArray()).Contains("UID:caller-event\r\n", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(
                resourceHref,
                "\"server-r2\"",
                Encoding.UTF8.GetBytes(
                    "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//dotnet-agents-caldav//EN\r\n"
                    + "BEGIN:VEVENT\r\nUID:caller-event\r\nDTSTAMP:20260816T120000Z\r\n"
                    + "CREATED:20260816T120000Z\r\nLAST-MODIFIED:20260816T120000Z\r\n"
                    + "SUMMARY:Planning\r\nDTSTART:20260817T130000Z\r\nDTEND:20260817T140000Z\r\n"
                    + "END:VEVENT\r\nEND:VCALENDAR\r\n")));

        var result = await sut.CreateEventAsync(
            new CalendarEventCreateRequest(
                CalendarCreateDestination.Default,
                "caller-event",
                new CalendarEventCreateFields(
                    Summary: "Planning",
                    Start: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T13:00:00Z"),
                    End: new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-17T14:00:00Z"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityCreateCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot.ShouldNotBeNull();
        result.Snapshot.EntityTag.ShouldBe("\"server-r2\"");
        result.Snapshot.Projection.EntityUid.ShouldBe("caller-event");
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    private static CalendarService CreateService(ICalendarClient client, string? defaultEventName = null) => new(
        client,
        Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            DefaultEventCalendarName = defaultEventName
        }),
        Substitute.For<ILogger<CalendarService>>());

    private static CalendarService CreateTodoService(
        ICalendarClient client,
        ICalendarEntityIdentityGenerator? identityGenerator = null) => new(
        client,
        Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            DefaultTodoCalendarName = "Todos"
        }),
        Substitute.For<ILogger<CalendarService>>(),
        TimeProvider.System,
        identityGenerator ?? Substitute.For<ICalendarEntityIdentityGenerator>());

    private static CalendarDescriptor EventCalendar(string href, string name) => new()
    {
        Href = href,
        DisplayName = name,
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.Advertised,
        TodoSupport = EntityKindSupport.NotAdvertised
    };

    private static CalendarDescriptor TodoCalendar(string href, string name) => new()
    {
        Href = href,
        DisplayName = name,
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.NotAdvertised,
        TodoSupport = EntityKindSupport.Advertised
    };

    private static byte[] Event(string uid, string summary) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//dotnet-agents-caldav//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260816T120000Z\r\n"
        + $"SUMMARY:{summary}\r\nDTSTART:20260817T130000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] Todo(string uid, string summary) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//dotnet-agents-caldav//EN\r\n"
        + $"BEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260816T120000Z\r\n"
        + $"SUMMARY:{summary}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

    private static CalendarTextValue Trigger(string value, params CalendarParameter[] parameters) =>
        new(value, parameters);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            utcNow += amount;
            _timestamp += amount.Ticks;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                period = newPeriod;
                _dueAt = newDueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner.GetUtcNow() + newDueTime;
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue()
            {
                if (_disposed || _dueAt is null || owner.GetUtcNow() < _dueAt)
                    return;
                _dueAt = period == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + period;
                callback(state);
            }
        }
    }
}
