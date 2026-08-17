using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarEntityPatchMatrixTests
{
    private const string EventHref = "https://cal.example/events/matrix.ics";
    private const string EventOriginal = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nX-KEEP;P=One,one:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
    private const string TodoHref = "https://cal.example/tasks/matrix.ics";
    private const string TodoOriginal = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nX-KEEP;P=One,one:opaque\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    [Fact]
    public async Task Event_scalar_families_edit_only_the_addressed_slice()
    {
        var organizer = new CalendarNamedUri(
            "mailto:owner@example.test",
            "Owner, One",
            [new CalendarParameter("X-ROLE", ["A:B"])]);
        var cases = new (CalendarEventPatch Patch, string Expected)[]
        {
            (new CalendarEventPatch(Description: Set("Detail, exact")), "DESCRIPTION:Detail\\, exact"),
            (new CalendarEventPatch(Start: Set(new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z"))), "DTSTART:20260821T100000Z"),
            (new CalendarEventPatch(Start: Set(new CalendarTemporalValue(CalendarTemporalKind.FloatingDateTime, "2026-08-21T10:30:00"))), "DTSTART:20260821T103000"),
            (new CalendarEventPatch(Start: Set(new CalendarTemporalValue(CalendarTemporalKind.ZonedDateTime, "2026-08-21T10:30:00", "America/Sao_Paulo"))), "DTSTART;TZID=America/Sao_Paulo:20260821T103000"),
            (new CalendarEventPatch(End: Set(new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-20T11:00:00Z"))), "DTEND:20260820T110000Z"),
            (new CalendarEventPatch(Duration: Set("PT1H")), "DURATION:PT1H"),
            (new CalendarEventPatch(Location: Set("Room; 1")), "LOCATION:Room\\; 1"),
            (new CalendarEventPatch(Geo: Set(new CalendarGeo(-23.5, -46.6))), "GEO:-23.5;-46.6"),
            (new CalendarEventPatch(Status: Set("confirmed")), "STATUS:CONFIRMED"),
            (new CalendarEventPatch(Transparency: Set("opaque")), "TRANSP:OPAQUE"),
            (new CalendarEventPatch(Classification: Set("private")), "CLASS:PRIVATE"),
            (new CalendarEventPatch(Priority: Set(5)), "PRIORITY:5"),
            (new CalendarEventPatch(Url: Set("https://example.test/e")), "URL:https://example.test/e"),
            (new CalendarEventPatch(Organizer: Set(organizer)),
                "ORGANIZER;CN=\"Owner, One\";X-ROLE=\"A:B\":mailto:owner@example.test")
        };

        foreach (var item in cases)
        {
            var execution = await ExecuteEventAsync(item.Patch);
            execution.Result.Code.ShouldBe(CalendarEntityPatchCode.Success, item.Expected);
            execution.Outbound.ShouldContain(item.Expected);
            execution.Outbound.ShouldContain("X-KEEP;P=One,one:opaque");
        }
    }

    [Fact]
    public async Task Event_start_only_patch_preserves_explicit_date_and_named_zone_effective_spans()
    {
        var cases = new[]
        {
            new
            {
                OriginalStart = "DTSTART;VALUE=DATE:20260820",
                OriginalEnd = "DTEND;VALUE=DATE;X-KEEP=end:20260822",
                Requested = new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-21"),
                ExpectedEnd = "DTEND;VALUE=DATE;X-KEEP=end:20260823"
            },
            new
            {
                OriginalStart = "DTSTART;TZID=America/New_York:20260307T100000",
                OriginalEnd = "DTEND;TZID=America/New_York;X-KEEP=end:20260308T100000",
                Requested = new CalendarTemporalValue(
                    CalendarTemporalKind.ZonedDateTime,
                    "2026-03-08T10:00:00",
                    "America/New_York"),
                ExpectedEnd = "DTEND;TZID=America/New_York;X-KEEP=end:20260309T090000"
            },
            new
            {
                OriginalStart = "DTSTART;VALUE=DATE:20260820",
                OriginalEnd = "DTEND;VALUE=DATE;X-KEEP=end:20260822",
                Requested = new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T12:00:00Z"),
                ExpectedEnd = "DTEND;X-KEEP=end:20260823T120000Z"
            }
        };
        foreach (var item in cases)
        {
            var original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\n"
                + item.OriginalStart + "\r\n" + item.OriginalEnd + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            var client = Client(EventHref, original, out var outbound);

            var result = await Service(client).PatchEventAsync(EventRequest(
                new CalendarEventPatch(Start: Set(item.Requested))), CancellationToken.None);

            result.Code.ShouldBe(CalendarEntityPatchCode.Success, item.ExpectedEnd);
            outbound().ShouldContain(item.ExpectedEnd + "\r\n");
        }
    }

    [Fact]
    public async Task Todo_start_only_patch_preserves_explicit_floating_and_utc_effective_spans()
    {
        var cases = new[]
        {
            new
            {
                OriginalStart = "DTSTART:20260820T100000",
                OriginalDue = "DUE;X-KEEP=due:20260820T113000",
                Requested = new CalendarTemporalValue(CalendarTemporalKind.FloatingDateTime, "2026-08-21T12:00:00"),
                ExpectedDue = "DUE;X-KEEP=due:20260821T133000"
            },
            new
            {
                OriginalStart = "DTSTART:20260820T100000Z",
                OriginalDue = "DUE;X-KEEP=due:20260820T110000Z",
                Requested = new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T12:00:00Z"),
                ExpectedDue = "DUE;X-KEEP=due:20260821T130000Z"
            },
            new
            {
                OriginalStart = "DTSTART:20260820T100000",
                OriginalDue = "DUE;X-KEEP=due:20260820T113000",
                Requested = new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T12:00:00Z"),
                ExpectedDue = "DUE;X-KEEP=due:20260821T133000Z"
            }
        };
        foreach (var item in cases)
        {
            var original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\n"
                + item.OriginalStart + "\r\n" + item.OriginalDue + "\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
            var client = Client(TodoHref, original, out var outbound);

            var result = await Service(client).PatchTodoAsync(new CalendarTodoPatchRequest(
                new(TodoHref, "matrix", CalendarEntityKind.Todo, "\"r1\""),
                new("master"),
                new CalendarTodoPatch(Start: Set(item.Requested))), CancellationToken.None);

            result.Code.ShouldBe(CalendarEntityPatchCode.Success, item.ExpectedDue);
            outbound().ShouldContain(item.ExpectedDue + "\r\n");
        }
    }

    [Theory]
    [InlineData("floating", "2026-08-21T12:00:00", "DTEND:20260822T120000")]
    [InlineData("utc", "2026-08-21T12:00:00Z", "DTEND:20260822T120000Z")]
    [InlineData("zoned", "2026-03-08T10:00:00", "DTEND;TZID=America/New_York:20260309T100000")]
    public async Task Implicit_date_event_span_is_preserved_across_temporal_family_changes(
        string kind,
        string value,
        string expectedEnd)
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART;VALUE=DATE:20260820\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var requested = kind switch
        {
            "floating" => new CalendarTemporalValue(CalendarTemporalKind.FloatingDateTime, value),
            "utc" => new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, value),
            _ => new CalendarTemporalValue(CalendarTemporalKind.ZonedDateTime, value, "America/New_York")
        };
        var client = Client(EventHref, original, out var outbound);

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Start: Set(requested))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        outbound().ShouldContain(expectedEnd + "\r\n");
    }

    [Fact]
    public async Task Start_only_patch_keeps_lexical_duration_and_does_not_invent_an_end()
    {
        const string withDuration = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDURATION:PT060M\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var durationClient = Client(EventHref, withDuration, out var durationOutbound);
        var durationResult = await Service(durationClient).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Start: Set(new CalendarTemporalValue(
                CalendarTemporalKind.UtcDateTime,
                "2026-08-21T12:00:00Z")))), CancellationToken.None);
        durationResult.Code.ShouldBe(CalendarEntityPatchCode.Success);
        durationOutbound().ShouldContain("DURATION:PT060M\r\n");

        var pointClient = Client(EventHref, EventOriginal, out var pointOutbound);
        var pointResult = await Service(pointClient).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Start: Set(new CalendarTemporalValue(
                CalendarTemporalKind.UtcDateTime,
                "2026-08-21T12:00:00Z")))), CancellationToken.None);
        pointResult.Code.ShouldBe(CalendarEntityPatchCode.Success);
        pointOutbound().ShouldNotContain("DTEND");
    }

    [Theory]
    [InlineData("floating", "2026-08-21T12:00:00")]
    [InlineData("utc", "2026-08-21T12:00:00Z")]
    [InlineData("zoned", "2026-03-08T10:00:00")]
    public async Task Date_event_with_duration_preserves_duration_without_synthesizing_end(
        string kind,
        string value)
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART;VALUE=DATE:20260820\r\nDURATION:P01D\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var requested = kind switch
        {
            "floating" => new CalendarTemporalValue(CalendarTemporalKind.FloatingDateTime, value),
            "utc" => new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, value),
            _ => new CalendarTemporalValue(CalendarTemporalKind.ZonedDateTime, value, "America/New_York")
        };
        var client = Client(EventHref, original, out var outbound);

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Start: Set(requested))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        outbound().ShouldContain("DURATION:P01D\r\n");
        outbound().ShouldNotContain("DTEND");
    }

    [Fact]
    public async Task Date_time_implicit_zero_span_cannot_be_changed_to_date()
    {
        var client = Client(EventHref, EventOriginal, out _);

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Start: Set(new CalendarTemporalValue(
                CalendarTemporalKind.Date,
                "2026-08-21")))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Todo_specific_scalar_families_are_typed_and_lossless()
    {
        var cases = new (CalendarTodoPatch Patch, string Expected)[]
        {
            (new CalendarTodoPatch(Due: Set(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-21"))), "DUE;VALUE=DATE:20260821"),
            (new CalendarTodoPatch(PercentComplete: Set(50)), "PERCENT-COMPLETE:50"),
            (new CalendarTodoPatch(Organizer: Set(new CalendarNamedUri("urn:uuid:owner", null, []))), "ORGANIZER:urn:uuid:owner")
        };
        foreach (var item in cases)
        {
            var execution = await ExecuteTodoAsync(item.Patch);
            execution.Result.Code.ShouldBe(CalendarEntityPatchCode.Success, item.Expected);
            execution.Outbound.ShouldContain(item.Expected);
            execution.Outbound.ShouldContain("X-KEEP;P=One,one:opaque");
        }
    }

    [Fact]
    public async Task Every_event_scalar_clear_removes_only_the_addressed_property()
    {
        var cases = new (CalendarEventPatch Patch, string Property)[]
        {
            (new CalendarEventPatch(Summary: Clear<string>()), "SUMMARY:Before"),
            (new CalendarEventPatch(Description: Clear<string>()), "DESCRIPTION:Before"),
            (new CalendarEventPatch(End: Clear<CalendarTemporalValue>()), "DTEND:20260820T110000Z"),
            (new CalendarEventPatch(Duration: Clear<string>()), "DURATION:PT1H"),
            (new CalendarEventPatch(Location: Clear<string>()), "LOCATION:Room"),
            (new CalendarEventPatch(Geo: Clear<CalendarGeo>()), "GEO:-23.5;-46.6"),
            (new CalendarEventPatch(Status: Clear<string>()), "STATUS:CONFIRMED"),
            (new CalendarEventPatch(Transparency: Clear<string>()), "TRANSP:OPAQUE"),
            (new CalendarEventPatch(Classification: Clear<string>()), "CLASS:PRIVATE"),
            (new CalendarEventPatch(Priority: Clear<int>()), "PRIORITY:5"),
            (new CalendarEventPatch(Url: Clear<string>()), "URL:https://example.test/e"),
            (new CalendarEventPatch(Organizer: Clear<CalendarNamedUri>()), "ORGANIZER:mailto:owner@example.test")
        };
        foreach (var item in cases)
        {
            var original = EventOriginal.Contains(item.Property, StringComparison.Ordinal)
                ? EventOriginal
                : EventOriginal.Replace("END:VEVENT", item.Property + "\r\nEND:VEVENT", StringComparison.Ordinal);
            var client = Client(EventHref, original, out var outbound);
            var result = await Service(client).PatchEventAsync(EventRequest(item.Patch), CancellationToken.None);
            result.Code.ShouldBe(CalendarEntityPatchCode.Success, item.Property);
            outbound().ShouldNotContain(item.Property);
            outbound().ShouldContain("X-KEEP;P=One,one:opaque");
        }


        var startClient = Client(EventHref, EventOriginal, out _);
        var invalidStartClear = await Service(startClient).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Start: Clear<CalendarTemporalValue>())), CancellationToken.None);
        invalidStartClear.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        await startClient.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Every_todo_specific_scalar_clear_is_lossless_and_absent_clear_is_no_change()
    {
        var cases = new (CalendarTodoPatch Patch, string Property)[]
        {
            (new CalendarTodoPatch(Due: Clear<CalendarTemporalValue>()), "DUE:20260820T100000Z"),
            (new CalendarTodoPatch(PercentComplete: Clear<int>()), "PERCENT-COMPLETE:50"),
            (new CalendarTodoPatch(Organizer: Clear<CalendarNamedUri>()), "ORGANIZER:mailto:owner@example.test")
        };
        foreach (var item in cases)
        {
            var original = TodoOriginal.Replace("END:VTODO", item.Property + "\r\nEND:VTODO", StringComparison.Ordinal);
            var client = Client(TodoHref, original, out var outbound);
            var result = await Service(client).PatchTodoAsync(new CalendarTodoPatchRequest(
                new(TodoHref, "matrix", CalendarEntityKind.Todo, "\"r1\""),
                new("master"),
                item.Patch), CancellationToken.None);
            result.Code.ShouldBe(CalendarEntityPatchCode.Success, item.Property);
            outbound().ShouldNotContain(item.Property);
            outbound().ShouldContain("X-KEEP;P=One,one:opaque");
        }

        var absentClient = Client(EventHref, EventOriginal, out _);
        var absent = await Service(absentClient).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Clear<string>())), CancellationToken.None);
        absent.Code.ShouldBe(CalendarEntityPatchCode.NoChange);
        await absentClient.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Every_structured_collection_maps_to_its_exact_property_or_component()
    {
        var text = new CalendarTextValue("Value", []);
        var named = new CalendarNamedUri("https://example.test/value", "Label", []);
        var uri = new CalendarUriValue("https://example.test/value", []);
        var attendee = new CalendarAttendee("mailto:person@example.test", []);
        var participant = new CalendarParticipant(new("participant-1", []), new("ACTIVE", []));
        var alarm = new CalendarAlarm(new("DISPLAY", []), new("-PT5M", []), new("Reminder", []));
        var cases = new (ICalendarCollectionPatch Patch, string Expected)[]
        {
            (Add(CalendarCollectionField.Attendees, attendee), "ATTENDEE:mailto:person@example.test"),
            (Add(CalendarCollectionField.Participants, participant), "BEGIN:PARTICIPANT"),
            (Add(CalendarCollectionField.Contacts, text), "CONTACT:Value"),
            (Add(CalendarCollectionField.Resources, text), "RESOURCES:Value"),
            (Add(CalendarCollectionField.RelatedTo, new CalendarRelation("parent", "PARENT")), "RELATED-TO;RELTYPE=PARENT:parent"),
            (Add(CalendarCollectionField.RequestStatuses, new CalendarRequestStatus("2.0", "Success")), "REQUEST-STATUS:2.0;Success"),
            (Add(CalendarCollectionField.Alarms, alarm), "BEGIN:VALARM"),
            (Add(CalendarCollectionField.Attachments, named), "ATTACH;LABEL=Label:https://example.test/value"),
            (Add(CalendarCollectionField.Comments, text), "COMMENT:Value"),
            (Add(CalendarCollectionField.StyledDescriptions, text), "STYLED-DESCRIPTION:Value"),
            (Add(CalendarCollectionField.Images, named), "IMAGE;LABEL=Label:https://example.test/value"),
            (Add(CalendarCollectionField.Conferences, named), "CONFERENCE;LABEL=Label:https://example.test/value"),
            (Add(CalendarCollectionField.Links, named), "LINK;LABEL=Label:https://example.test/value"),
            (Add(CalendarCollectionField.Concepts, uri), "CONCEPT:https://example.test/value"),
            (Add(CalendarCollectionField.StructuredDataUris, uri), "STRUCTURED-DATA;VALUE=URI:https://example.test/value"),
            (Add(CalendarCollectionField.LocationUris, named), "BEGIN:VLOCATION"),
            (Add(CalendarCollectionField.ResourceUris, named), "BEGIN:VRESOURCE")
        };
        foreach (var item in cases)
        {
            var execution = await ExecuteEventAsync(new CalendarEventPatch(Collections: [item.Patch]));
            execution.Result.Code.ShouldBe(CalendarEntityPatchCode.Success, item.Expected);
            execution.Outbound.ShouldContain(item.Expected);
        }
    }

    [Fact]
    public async Task Ambiguous_late_collection_removal_rolls_back_earlier_scalar_in_memory()
    {
        const string ambiguous = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nSUMMARY:Before\r\nATTENDEE:mailto:person@example.test\r\nATTENDEE:mailto:person@example.test\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var attendee = new CalendarAttendee("mailto:person@example.test", []);
        var client = Client(EventHref, ambiguous, out _);
        var result = await Service(client).PatchEventAsync(EventRequest(new CalendarEventPatch(
            Summary: new(CalendarScalarPatchOperation.Set, "After"),
            Collections: [new CalendarCollectionPatch<CalendarAttendee>(
                CalendarCollectionPatchOperation.AddRemove,
                Remove: [attendee],
                Field: CalendarCollectionField.Attendees)])), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.RemovalAmbiguous);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unambiguous_structured_removal_removes_only_the_exact_occurrence()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nATTENDEE:mailto:remove@example.test\r\nX-KEEP;P=One,one:opaque\r\nATTENDEE:mailto:keep@example.test\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Client(EventHref, original, out var outbound);
        var removal = new CalendarCollectionPatch<CalendarAttendee>(
            CalendarCollectionPatchOperation.AddRemove,
            Remove: [new CalendarAttendee("mailto:remove@example.test", [])],
            Field: CalendarCollectionField.Attendees);

        var result = await Service(client).PatchEventAsync(
            EventRequest(new CalendarEventPatch(Collections: [removal])), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        outbound().ShouldNotContain("ATTENDEE:mailto:remove@example.test");
        outbound().ShouldContain("ATTENDEE:mailto:keep@example.test");
        outbound().ShouldContain("X-KEEP;P=One,one:opaque");
    }

    [Fact]
    public async Task Every_structured_collection_semantically_round_trips_for_exact_lossless_removal()
    {
        var text = new CalendarTextValue("Value", []);
        var named = new CalendarNamedUri("https://example.test/value", "Label", []);
        var uri = new CalendarUriValue("https://example.test/value", []);
        var attendee = new CalendarAttendee("mailto:person@example.test", []);
        var participant = new CalendarParticipant(new("participant-1", []), new("ACTIVE", []));
        var alarm = new CalendarAlarm(new("DISPLAY", []), new("-PT5M", []), new("Reminder", []));
        var cases = new ICalendarCollectionPatch[]
        {
            Remove(CalendarCollectionField.Attendees, attendee),
            Remove(CalendarCollectionField.Participants, participant),
            Remove(CalendarCollectionField.Contacts, text),
            Remove(CalendarCollectionField.Resources, text),
            Remove(CalendarCollectionField.RelatedTo, new CalendarRelation("parent", "PARENT")),
            Remove(CalendarCollectionField.RequestStatuses, new CalendarRequestStatus("2.0", "Success")),
            Remove(CalendarCollectionField.Alarms, alarm),
            Remove(CalendarCollectionField.Attachments, named),
            Remove(CalendarCollectionField.Comments, text),
            Remove(CalendarCollectionField.StyledDescriptions, text),
            Remove(CalendarCollectionField.Images, named),
            Remove(CalendarCollectionField.Conferences, named),
            Remove(CalendarCollectionField.Links, named),
            Remove(CalendarCollectionField.Concepts, uri),
            Remove(CalendarCollectionField.StructuredDataUris, uri),
            Remove(CalendarCollectionField.LocationUris, named),
            Remove(CalendarCollectionField.ResourceUris, named)
        };

        foreach (var patch in cases)
        {
            var occurrence = CalendarPatchOccurrenceSerializer.Serialize(
                patch.Field,
                patch.RemoveValues!.Single(),
                CalendarEntityKind.Event);
            var original = EventOriginal.Replace("END:VEVENT", occurrence + "END:VEVENT", StringComparison.Ordinal);
            var client = Client(EventHref, original, out var outbound);

            var result = await Service(client).PatchEventAsync(EventRequest(
                new CalendarEventPatch(Collections: [patch])), CancellationToken.None);

            result.Code.ShouldBe(CalendarEntityPatchCode.Success, patch.Field.ToString());
            outbound().ShouldNotContain(occurrence);
            outbound().ShouldContain("X-KEEP;P=One,one:opaque");
        }
    }

    [Fact]
    public async Task Structured_removal_with_zero_semantic_matches_rejects_without_write()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nATTENDEE:mailto:keep@example.test\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Client(EventHref, original, out _);
        var removal = Remove(
            CalendarCollectionField.Attendees,
            new CalendarAttendee("mailto:missing@example.test", []));

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Collections: [removal])), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.RemovalNotFound);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remove_then_readd_of_the_same_ordered_occurrence_is_no_change(bool categories)
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nATTENDEE;CN=Person:mailto:person@example.test\r\nCATEGORIES:Work\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Client(EventHref, original, out _);
        var patch = categories
            ? new CalendarEventPatch(Categories: new(
                CalendarCollectionPatchOperation.AddRemove,
                Add: ["Work"],
                Remove: ["Work"]))
            : new CalendarEventPatch(Collections: [new CalendarCollectionPatch<CalendarAttendee>(
                CalendarCollectionPatchOperation.AddRemove,
                Add: [new CalendarAttendee("mailto:person@example.test", [], CommonName: "Person")],
                Remove: [new CalendarAttendee("mailto:person@example.test", [], CommonName: "Person")],
                Field: CalendarCollectionField.Attendees)]);

        var result = await Service(client).PatchEventAsync(EventRequest(patch), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.NoChange);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(-1)]
    public async Task Invalid_scalar_intent_is_rejected_after_selection_and_before_write(int priority)
    {
        var client = Client(EventHref, EventOriginal, out _);

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Priority: Set(priority))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        await client.Received(1).GetCalendarResourceAsync(EventHref, Arg.Any<CancellationToken>());
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Patch_that_would_leave_end_and_duration_is_atomically_rejected()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Client(EventHref, original, out _);

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Duration: Set("PT1H"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Event_patch_validates_the_complete_resulting_temporal_tuple_before_write()
    {
        const string withEnd = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var shiftedClient = Client(EventHref, withEnd, out var shiftedOutbound);
        var shifted = await Service(shiftedClient).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Start: Set(new CalendarTemporalValue(
                CalendarTemporalKind.UtcDateTime,
                "2026-08-20T12:00:00Z")))), CancellationToken.None);
        shifted.Code.ShouldBe(CalendarEntityPatchCode.Success);
        shiftedOutbound().ShouldContain("DTEND:20260820T130000Z");

        var familyClient = Client(EventHref, withEnd, out _);
        var invalidFamily = await Service(familyClient).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Start: Set(new CalendarTemporalValue(
                CalendarTemporalKind.Date,
                "2026-08-20")))), CancellationToken.None);
        invalidFamily.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        await familyClient.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());

        var durationClient = Client(EventHref, EventOriginal, out _);
        var invalidDuration = await Service(durationClient).PatchEventAsync(EventRequest(
            new CalendarEventPatch(
                Start: Set(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-20")),
                Duration: Set("PT1H"))), CancellationToken.None);
        invalidDuration.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        await durationClient.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Todo_patch_validates_the_complete_resulting_temporal_tuple_before_write()
    {
        const string withDue = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDUE:20260820T110000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var invalidStarts = new[]
        {
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-20T12:00:00Z"),
            new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-20")
        };
        foreach (var start in invalidStarts)
        {
            var client = Client(TodoHref, withDue, out _);
            var result = await Service(client).PatchTodoAsync(new CalendarTodoPatchRequest(
                new(TodoHref, "matrix", CalendarEntityKind.Todo, "\"r1\""),
                new("master"),
                new CalendarTodoPatch(Start: Set(start))), CancellationToken.None);
            result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
            await client.DidNotReceive().UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        }

        const string withDuration = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDURATION:PT1H\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var clearClient = Client(TodoHref, withDuration, out _);
        var invalidClear = await Service(clearClient).PatchTodoAsync(new CalendarTodoPatchRequest(
            new(TodoHref, "matrix", CalendarEntityKind.Todo, "\"r1\""),
            new("master"),
            new CalendarTodoPatch(Start: Clear<CalendarTemporalValue>())), CancellationToken.None);
        invalidClear.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        await clearClient.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unrelated_patch_preserves_an_existing_unresolved_time_zone()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART;TZID=Private/Unknown:20260820T100000\r\nDURATION:PT1H\r\nSUMMARY:Before\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Client(EventHref, original, out var outbound);

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        outbound().ShouldContain("DTSTART;TZID=Private/Unknown:20260820T100000\r\n");
        outbound().ShouldContain("DURATION:PT1H\r\n");
    }

    [Fact]
    public async Task Post_write_property_reordering_is_success_but_unknown_drift_is_fidelity_failure()
    {
        var normalized = EventOriginal.Replace(
            "DTSTART:20260820T100000Z\r\nX-KEEP;P=One,one:opaque\r\n",
            "X-KEEP;P=One,one:opaque\r\nDTSTART:20260820T100000Z\r\n",
            StringComparison.Ordinal);
        var success = await ExecuteEventAsync(
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "After")),
            observedTransform: outbound => normalized.Replace(
                "END:VEVENT",
                "SUMMARY:After\r\nLAST-MODIFIED:20260817T120000Z\r\nEND:VEVENT",
                StringComparison.Ordinal));
        success.Result.Code.ShouldBe(CalendarEntityPatchCode.Success);

        var drift = await ExecuteEventAsync(
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "After")),
            observedTransform: outbound => outbound.Replace("opaque", "drift", StringComparison.Ordinal));
        drift.Result.Code.ShouldBe(CalendarEntityPatchCode.FidelityFailure);
        drift.Result.MutationState.ShouldBe(CalendarMutationState.Committed);
    }

    [Fact]
    public async Task Post_write_fidelity_compares_master_derived_and_override_last_modified_occurrences()
    {
        var master = await ExecuteEventAsync(
            new CalendarEventPatch(Summary: Set("After")),
            observedTransform: outbound => outbound.Replace(
                "LAST-MODIFIED:20260817T120000Z",
                "LAST-MODIFIED:20260817T120001Z",
                StringComparison.Ordinal));
        master.Result.Code.ShouldBe(CalendarEntityPatchCode.FidelityFailure);

        const string derivedOriginal = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nLAST-MODIFIED;DERIVED=TRUE:20260815T100000Z\r\nSUMMARY:Before\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var derivedClient = Client(
            EventHref,
            derivedOriginal,
            out _,
            observedTransform: outbound => outbound.Replace(
                "LAST-MODIFIED;DERIVED=TRUE:20260815T100000Z",
                "LAST-MODIFIED;DERIVED=TRUE:20260815T100001Z",
                StringComparison.Ordinal));
        var derived = await Service(derivedClient).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);
        derived.Code.ShouldBe(CalendarEntityPatchCode.FidelityFailure);

        const string recurring = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Before\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T100000Z\r\nLAST-MODIFIED:20260815T110000Z\r\nSUMMARY:Override\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var overrideClient = Client(
            EventHref,
            recurring,
            out _,
            observedTransform: outbound => outbound.Replace(
                "LAST-MODIFIED:20260815T110000Z",
                "LAST-MODIFIED:20260815T110001Z",
                StringComparison.Ordinal));
        var overrideResult = await Service(overrideClient).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);
        overrideResult.Code.ShouldBe(CalendarEntityPatchCode.FidelityFailure);
    }

    [Theory]
    [InlineData("committed", CalendarEntityPatchCode.Success, CalendarMutationState.Committed)]
    [InlineData("unchanged", CalendarEntityPatchCode.UpstreamUnavailable, CalendarMutationState.NotCommitted)]
    [InlineData("diverged", CalendarEntityPatchCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task Possibly_dispatched_put_is_reconciled_without_blind_retry(
        string observation,
        CalendarEntityPatchCode expectedCode,
        CalendarMutationState expectedState)
    {
        var transform = observation switch
        {
            "committed" => (Func<string, string>)(outbound => outbound),
            _ => _ => EventOriginal
        };
        var observedTag = observation == "unchanged" ? "\"r1\"" : "\"r2\"";
        var client = Client(
            EventHref,
            EventOriginal,
            out _,
            transform,
            CalendarResourceUpdateDispatchCode.PossiblyDispatched,
            observedTag);

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceUpdateDispatchCode.Dispatched)]
    [InlineData(CalendarResourceUpdateDispatchCode.PossiblyDispatched)]
    public async Task Changed_body_with_unchanged_strong_tag_is_committed_but_concurrency_unavailable(
        CalendarResourceUpdateDispatchCode dispatchCode)
    {
        var client = Client(
            EventHref,
            EventOriginal,
            out _,
            observedTransform: outbound => outbound,
            dispatchCode,
            observedEntityTag: "\"r1\"");

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.CommittedButConcurrencyUnavailable);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot.ShouldBeNull();
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceUpdateDispatchCode.Dispatched, CalendarEntityPatchCode.FidelityFailure, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceUpdateDispatchCode.PossiblyDispatched, CalendarEntityPatchCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task Opaque_post_write_projection_never_proves_patch_success(
        CalendarResourceUpdateDispatchCode dispatchCode,
        CalendarEntityPatchCode expectedCode,
        CalendarMutationState expectedState)
    {
        var client = Substitute.For<ICalendarClient>();
        ReadOnlyMemory<byte> written = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => written = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(dispatchCode));
        var current = CalendarResourceProjector.AttachSnapshot(
            "https://cal.example/tasks/",
            CalendarResourceRead.Success(TodoHref, "\"r1\"", Encoding.UTF8.GetBytes(TodoOriginal)));
        var reads = 0;
        Task<CalendarResourceRead> Read(string _, CalendarEntityKind __, CancellationToken ___)
        {
            reads++;
            if (reads == 1)
                return Task.FromResult(current);
            var opaque = current.Snapshot! with
            {
                EntityTag = "\"r2\"",
                AuthoritativeUtf8 = written,
                Projection = new(CalendarResourceProjectionKind.Opaque, null, null)
            };
            return Task.FromResult(new CalendarResourceRead(
                CalendarResourceReadCode.Success,
                TodoHref,
                "\"r2\"",
                written,
                opaque));
        }
        var engine = new CalendarEntityPatchEngine(client, Read, new FrozenTimeProvider());

        var result = await engine.PatchTodoAsync(new CalendarTodoPatchRequest(
            new(TodoHref, "matrix", CalendarEntityKind.Todo, "\"r1\""),
            new("master"),
            new CalendarTodoPatch(Summary: Set("After"))), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
    }

    [Fact]
    public async Task Conditional_conflict_refreshes_current_snapshot_without_retry()
    {
        var client = Client(
            EventHref,
            EventOriginal,
            out _,
            _ => EventOriginal.Replace("matrix", "other", StringComparison.Ordinal),
            CalendarResourceUpdateDispatchCode.Conflict,
            "\"r2\"");

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        result.Snapshot!.EntityTag.ShouldBe("\"r2\"");
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceUpdateDispatchCode.NotFound, CalendarEntityPatchCode.NotFound)]
    [InlineData(CalendarResourceUpdateDispatchCode.InvalidInput, CalendarEntityPatchCode.InvalidInput)]
    [InlineData(CalendarResourceUpdateDispatchCode.UnsupportedCapability, CalendarEntityPatchCode.UnsupportedCapability)]
    [InlineData(CalendarResourceUpdateDispatchCode.PayloadTooLarge, CalendarEntityPatchCode.PayloadTooLarge)]
    [InlineData(CalendarResourceUpdateDispatchCode.UpstreamUnauthorized, CalendarEntityPatchCode.UpstreamUnauthorized)]
    [InlineData(CalendarResourceUpdateDispatchCode.UpstreamForbidden, CalendarEntityPatchCode.UpstreamForbidden)]
    [InlineData(CalendarResourceUpdateDispatchCode.UpstreamRateLimited, CalendarEntityPatchCode.UpstreamRateLimited)]
    [InlineData(CalendarResourceUpdateDispatchCode.UpstreamUnavailable, CalendarEntityPatchCode.UpstreamUnavailable)]
    [InlineData(CalendarResourceUpdateDispatchCode.UpstreamProtocolError, CalendarEntityPatchCode.UpstreamProtocolError)]
    public async Task Definitive_put_rejections_preserve_not_committed_truth(
        CalendarResourceUpdateDispatchCode dispatch,
        CalendarEntityPatchCode expected)
    {
        var client = Client(EventHref, EventOriginal, out _, dispatchCode: dispatch);

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);

        result.Code.ShouldBe(expected);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(EventHref, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.InvalidInput, CalendarEntityPatchCode.InvalidInput)]
    [InlineData(CalendarResourceReadCode.NotFound, CalendarEntityPatchCode.NotFound)]
    [InlineData(CalendarResourceReadCode.OutsideScope, CalendarEntityPatchCode.OutsideScope)]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable, CalendarEntityPatchCode.ConcurrencyUnavailable)]
    [InlineData(CalendarResourceReadCode.PayloadTooLarge, CalendarEntityPatchCode.PayloadTooLarge)]
    [InlineData(CalendarResourceReadCode.UpstreamProtocolError, CalendarEntityPatchCode.UpstreamProtocolError)]
    public async Task Preflight_read_failures_never_dispatch(
        CalendarResourceReadCode readCode,
        CalendarEntityPatchCode expected)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = "https://cal.example/events/",
                DisplayName = "Matrix",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);
        client.GetCalendarResourceAsync(EventHref, Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceRead(readCode));

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);

        result.Code.ShouldBe(expected);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Phase.ShouldBe(readCode switch
        {
            CalendarResourceReadCode.NotFound => CalendarEntityPatchPhase.SelectionDiscoveryCapability,
            CalendarResourceReadCode.OutsideScope => CalendarEntityPatchPhase.OriginScopeAuthorization,
            CalendarResourceReadCode.ConcurrencyUnavailable => CalendarEntityPatchPhase.TargetRevision,
            CalendarResourceReadCode.PayloadTooLarge => CalendarEntityPatchPhase.AdmissionAndPayload,
            CalendarResourceReadCode.UpstreamProtocolError => CalendarEntityPatchPhase.SelectionDiscoveryCapability,
            _ => CalendarEntityPatchPhase.CompleteResourceSemantics
        });
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Out_of_scope_origin_precedes_weak_revision_and_invalid_complete_semantics()
    {
        const string outsideHref = "https://cal.example/outside/event.ics";
        var client = Substitute.For<ICalendarClient>();
        var service = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example/",
                Username = "u",
                Password = "p",
                CalendarHrefs = "https://cal.example/allowed/"
            }),
            Substitute.For<ILogger<CalendarService>>(),
            new FrozenTimeProvider(),
            Substitute.For<ICalendarEntityIdentityGenerator>());
        var request = new CalendarEventPatchRequest(
            new(outsideHref, "matrix", CalendarEntityKind.Event, "W/\"r1\""),
            new("master"),
            new CalendarEventPatch(Priority: Set(10)));

        var result = await service.PatchEventAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.OutsideScope);
        result.Phase.ShouldBe(CalendarEntityPatchPhase.OriginScopeAuthorization);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("\"r2\"", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n")]
    [InlineData("\"r1\"", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:other\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")]
    public async Task Stale_revision_precedes_opaque_and_invalid_complete_semantics(
        string authoritativeTag,
        string authoritativeContent)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = "https://cal.example/events/",
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            }
        ]);
        client.GetCalendarResourceAsync(EventHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(
                EventHref,
                authoritativeTag,
                Encoding.UTF8.GetBytes(authoritativeContent)));

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Priority: Set(10))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Conflict);
        result.Phase.ShouldBe(CalendarEntityPatchPhase.TargetRevision);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarEntityKind.Event, EntityKindSupport.NotAdvertised, CalendarEntityPatchCode.UnsupportedCapability)]
    [InlineData(CalendarEntityKind.Todo, EntityKindSupport.NotAdvertised, CalendarEntityPatchCode.UnsupportedCapability)]
    [InlineData(CalendarEntityKind.Event, EntityKindSupport.Unknown, CalendarEntityPatchCode.Success)]
    [InlineData(CalendarEntityKind.Todo, EntityKindSupport.Unknown, CalendarEntityPatchCode.Success)]
    public async Task Patch_enforces_selected_calendar_kind_support_without_narrowing_unknown_policy(
        CalendarEntityKind kind,
        EntityKindSupport support,
        CalendarEntityPatchCode expected)
    {
        var href = kind == CalendarEntityKind.Event ? EventHref : TodoHref;
        var component = kind == CalendarEntityKind.Event ? "VEVENT" : "VTODO";
        var original = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:{component}\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\n"
            + (kind == CalendarEntityKind.Event ? "DTSTART:20260820T100000Z\r\n" : string.Empty)
            + $"SUMMARY:Before\r\nEND:{component}\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        ReadOnlyMemory<byte> written = default;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = href[..(href.LastIndexOf('/') + 1)],
                DisplayName = "Matrix",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = kind == CalendarEntityKind.Event ? support : EntityKindSupport.Advertised,
                TodoSupport = kind == CalendarEntityKind.Todo ? support : EntityKindSupport.Advertised
            }
        ]);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(
            _ => CalendarResourceRead.Success(href, "\"r1\"", Encoding.UTF8.GetBytes(original)),
            _ => CalendarResourceRead.Success(
                href,
                "\"r2\"",
                written));
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => written = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        var service = Service(client);

        var result = kind == CalendarEntityKind.Event
            ? await service.PatchEventAsync(EventRequest(new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None)
            : await service.PatchTodoAsync(new CalendarTodoPatchRequest(
                new(TodoHref, "matrix", CalendarEntityKind.Todo, "\"r1\""),
                new("master"),
                new CalendarTodoPatch(Summary: Set("After"))), CancellationToken.None);

        result.Code.ShouldBe(expected);
        if (support == EntityKindSupport.NotAdvertised)
        {
            result.Phase.ShouldBe(CalendarEntityPatchPhase.SelectionDiscoveryCapability);
            result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
            await client.DidNotReceive().GetCalendarResourceAsync(href, Arg.Any<CancellationToken>());
            await client.DidNotReceive().UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, CalendarEntityPatchCode.UpstreamUnauthorized, false)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, CalendarEntityPatchCode.UpstreamForbidden, false)]
    [InlineData(System.Net.HttpStatusCode.Conflict, CalendarEntityPatchCode.Conflict, false)]
    [InlineData(System.Net.HttpStatusCode.PreconditionFailed, CalendarEntityPatchCode.Conflict, false)]
    [InlineData(System.Net.HttpStatusCode.RequestEntityTooLarge, CalendarEntityPatchCode.PayloadTooLarge, false)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, CalendarEntityPatchCode.UpstreamRateLimited, true)]
    [InlineData(System.Net.HttpStatusCode.MethodNotAllowed, CalendarEntityPatchCode.UnsupportedCapability, false)]
    [InlineData(System.Net.HttpStatusCode.NotImplemented, CalendarEntityPatchCode.UnsupportedCapability, false)]
    [InlineData(System.Net.HttpStatusCode.NotFound, CalendarEntityPatchCode.UpstreamProtocolError, false)]
    [InlineData((System.Net.HttpStatusCode)507, CalendarEntityPatchCode.UpstreamUnavailable, false)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, CalendarEntityPatchCode.UpstreamUnavailable, true)]
    public async Task Preflight_http_failures_are_sanitized_and_mapped_before_write(
        System.Net.HttpStatusCode status,
        CalendarEntityPatchCode expected,
        bool retryable)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromException<IReadOnlyList<CalendarDescriptor>>(new HttpRequestException(
                "secret upstream response",
                inner: null,
                status)));

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);

        result.Code.ShouldBe(expected);
        result.Retryable.ShouldBe(retryable);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Phase.ShouldBe(CalendarEntityPatchPhase.SelectionDiscoveryCapability);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("limit", CalendarEntityPatchCode.LimitExhausted)]
    [InlineData("protocol", CalendarEntityPatchCode.UpstreamProtocolError)]
    [InlineData("xml", CalendarEntityPatchCode.UpstreamProtocolError)]
    [InlineData("unsupported", CalendarEntityPatchCode.UnsupportedCapability)]
    public async Task Preflight_discovery_failures_are_typed_before_write(
        string failure,
        CalendarEntityPatchCode expected)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<CalendarDescriptor>>(_ =>
            throw failure switch
            {
                "limit" => new CalendarDiscoveryLimitException(257),
                "protocol" => new CalendarDiscoveryProtocolException("secret protocol"),
                "xml" => new System.Xml.XmlException("secret XML"),
                _ => new CalendarDiscoveryUnsupportedCapabilityException("secret unsupported")
            });

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);

        result.Code.ShouldBe(expected);
        result.Retryable.ShouldBeFalse();
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Phase.ShouldBe(CalendarEntityPatchPhase.SelectionDiscoveryCapability);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verification_and_reconciliation_failures_preserve_truth_without_retry()
    {
        var missing = Client(EventHref, EventOriginal, out _);
        missing.GetCalendarResourceAsync(EventHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(EventHref, "\"r1\"", Encoding.UTF8.GetBytes(EventOriginal)),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        var unverified = await Service(missing).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);
        unverified.Code.ShouldBe(CalendarEntityPatchCode.CommittedButUnverified);
        unverified.MutationState.ShouldBe(CalendarMutationState.Committed);

        var ambiguous = Client(
            EventHref,
            EventOriginal,
            out _,
            dispatchCode: CalendarResourceUpdateDispatchCode.PossiblyDispatched);
        ambiguous.GetCalendarResourceAsync(EventHref, Arg.Any<CancellationToken>()).Returns(
            _ => CalendarResourceRead.Success(EventHref, "\"r1\"", Encoding.UTF8.GetBytes(EventOriginal)),
            _ => throw new IOException("lost during reconciliation"));
        var indeterminate = await Service(ambiguous).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);
        indeterminate.Code.ShouldBe(CalendarEntityPatchCode.Indeterminate);
        indeterminate.MutationState.ShouldBe(CalendarMutationState.Unknown);

        await missing.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        await ambiguous.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unambiguous_category_removal_preserves_the_property_header_and_retained_order()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nCATEGORIES;LANGUAGE=en:First,Remove,Last\r\nX-KEEP;P=One,one:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Client(EventHref, original, out var outbound);

        var result = await Service(client).PatchEventAsync(EventRequest(new CalendarEventPatch(
            Categories: new CalendarCollectionPatch<string>(
                CalendarCollectionPatchOperation.AddRemove,
                Remove: ["Remove"]))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        outbound().ShouldContain("CATEGORIES;LANGUAGE=en:First,Last");
        outbound().ShouldContain("X-KEEP;P=One,one:opaque");
    }

    [Fact]
    public async Task Structured_replaceAll_replaces_all_addressed_occurrences_and_preserves_neighbors()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:matrix\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nCONTACT:First\r\nX-KEEP;P=One,one:opaque\r\nCONTACT:Second\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Client(EventHref, original, out var outbound);
        var replacement = new CalendarCollectionPatch<CalendarTextValue>(
            CalendarCollectionPatchOperation.ReplaceAll,
            Values: [new CalendarTextValue("Only", [])],
            Field: CalendarCollectionField.Contacts);

        var result = await Service(client).PatchEventAsync(EventRequest(
            new CalendarEventPatch(Collections: [replacement])), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        outbound().ShouldContain("CONTACT:Only");
        outbound().ShouldNotContain("CONTACT:First");
        outbound().ShouldNotContain("CONTACT:Second");
        outbound().ShouldContain("X-KEEP;P=One,one:opaque");
    }

    [Fact]
    public async Task Every_invalid_event_intent_family_is_rejected_after_selection_and_before_write()
    {
        var invalid = new CalendarEventPatch[]
        {
            new(),
            new(Priority: Set(10)),
            new(Start: Set(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-02-30"))),
            new(Duration: Set("PT0S")),
            new(Geo: Set(new CalendarGeo(91, 0))),
            new(Status: Set("not a token")),
            new(Transparency: Set("invalid")),
            new(Classification: Set("invalid")),
            new(Url: Set("relative")),
            new(Organizer: Set(new CalendarNamedUri("relative", null, []))),
            new(Due: Set(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-21"))),
            new(PercentComplete: Set(50)),
            new(RecurrenceSetAddressed: true),
            new(RequiresConfirmation: true),
            new(Categories: new CalendarCollectionPatch<string>(CalendarCollectionPatchOperation.AddRemove)),
            new(Collections: []),
            new(Collections: [new CalendarCollectionPatch<CalendarTextValue>(
                CalendarCollectionPatchOperation.AddRemove,
                Add: [new CalendarTextValue("Value", [])],
                Field: CalendarCollectionField.Categories)])
        };

        foreach (var patch in invalid)
        {
            var client = Client(EventHref, EventOriginal, out _);
            var result = await Service(client).PatchEventAsync(EventRequest(patch), CancellationToken.None);
            result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput, patch.ToString());
            await client.Received(1).GetCalendarResourceAsync(EventHref, Arg.Any<CancellationToken>());
            await client.DidNotReceive().UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task Invalid_todo_specific_values_are_rejected_after_selection_and_before_write()
    {
        var invalid = new CalendarTodoPatch[]
        {
            new(PercentComplete: Set(-1)),
            new(PercentComplete: Set(101))
        };
        foreach (var patch in invalid)
        {
            var client = Client(TodoHref, TodoOriginal, out _);
            var result = await Service(client).PatchTodoAsync(new CalendarTodoPatchRequest(
                new(TodoHref, "matrix", CalendarEntityKind.Todo, "\"r1\""),
                new("master"),
                patch), CancellationToken.None);
            result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput, patch.ToString());
            await client.Received(1).GetCalendarResourceAsync(TodoHref, Arg.Any<CancellationToken>());
            await client.DidNotReceive().UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task Todo_patch_rejects_completed_status_reserved_for_coordinated_completion()
    {
        var client = Client(TodoHref, TodoOriginal, out _);

        var result = await Service(client).PatchTodoAsync(new CalendarTodoPatchRequest(
            new(TodoHref, "matrix", CalendarEntityKind.Todo, "\"r1\""),
            new("master"),
            new CalendarTodoPatch(Status: Set("COMPLETED"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.Phase.ShouldBe(CalendarEntityPatchPhase.CompleteResourceSemantics);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unrelated_todo_patch_preserves_existing_completion_state_byte_exact()
    {
        var original = TodoOriginal.Replace(
            "END:VTODO",
            "STATUS:COMPLETED\r\nCOMPLETED;X-KEEP=exact:20260820T100000Z\r\nEND:VTODO",
            StringComparison.Ordinal);
        var client = Client(TodoHref, original, out var outbound);

        var result = await Service(client).PatchTodoAsync(new CalendarTodoPatchRequest(
            new(TodoHref, "matrix", CalendarEntityKind.Todo, "\"r1\""),
            new("master"),
            new CalendarTodoPatch(Summary: Set("After"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        outbound().ShouldContain("STATUS:COMPLETED\r\nCOMPLETED;X-KEEP=exact:20260820T100000Z\r\n");
    }

    [Fact]
    public async Task Caller_cancellation_propagates_but_pre_dispatch_deadline_is_typed_and_never_writes()
    {
        var canceledClient = Substitute.For<ICalendarClient>();
        canceledClient.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromException<IReadOnlyList<CalendarDescriptor>>(new OperationCanceledException()));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => Service(canceledClient).PatchEventAsync(
            EventRequest(new CalendarEventPatch(Summary: Set("After"))), caller.Token));

        var deadlineClient = Substitute.For<ICalendarClient>();
        var pendingDiscovery = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        deadlineClient.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(pendingDiscovery.Task);
        deadlineClient.When(client => client.GetCalendarsAsync(Arg.Any<CancellationToken>())).Do(call =>
            call.Arg<CancellationToken>().Register(() =>
                pendingDiscovery.TrySetCanceled(call.Arg<CancellationToken>())));
        var time = new ControllableTimeProvider();
        var pending = Service(deadlineClient, time).PatchEventAsync(
            EventRequest(new CalendarEventPatch(Summary: Set("After"))), CancellationToken.None);
        time.FireTimers();

        var result = await pending;
        result.Code.ShouldBe(CalendarEntityPatchCode.LimitExhausted);
        result.Retryable.ShouldBeFalse();
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Phase.ShouldBe(CalendarEntityPatchPhase.Execution);
        result.LimitDimension.ShouldBe(CalendarEntityPatchLimitDimension.ElapsedTime);
        await deadlineClient.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    private static CalendarCollectionPatch<T> Add<T>(CalendarCollectionField field, T value) => new(
        CalendarCollectionPatchOperation.AddRemove,
        Add: [value],
        Field: field);

    private static CalendarCollectionPatch<T> Remove<T>(CalendarCollectionField field, T value) => new(
        CalendarCollectionPatchOperation.AddRemove,
        Remove: [value],
        Field: field);

    private static CalendarScalarPatch<T> Set<T>(T value) =>
        new(CalendarScalarPatchOperation.Set, value);

    private static CalendarScalarPatch<T> Clear<T>() => new(CalendarScalarPatchOperation.Clear);

    private static async Task<(CalendarEntityPatchResult Result, string Outbound)> ExecuteEventAsync(
        CalendarEventPatch patch,
        Func<string, string>? observedTransform = null)
    {
        var client = Client(EventHref, EventOriginal, out var outbound, observedTransform);
        var result = await Service(client).PatchEventAsync(EventRequest(patch), CancellationToken.None);
        return (result, outbound());
    }

    private static async Task<(CalendarEntityPatchResult Result, string Outbound)> ExecuteTodoAsync(CalendarTodoPatch patch)
    {
        var client = Client(TodoHref, TodoOriginal, out var outbound);
        var result = await Service(client).PatchTodoAsync(new CalendarTodoPatchRequest(
            new(TodoHref, "matrix", CalendarEntityKind.Todo, "\"r1\""),
            new("master"),
            patch), CancellationToken.None);
        return (result, outbound());
    }

    private static ICalendarClient Client(
        string href,
        string original,
        out Func<string> outbound,
        Func<string, string>? observedTransform = null,
        CalendarResourceUpdateDispatchCode dispatchCode = CalendarResourceUpdateDispatchCode.Dispatched,
        string observedEntityTag = "\"r2\"")
    {
        var client = Substitute.For<ICalendarClient>();
        var written = string.Empty;
        var reads = 0;
        var calendarHref = href[..(href.LastIndexOf('/') + 1)];
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Matrix",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            reads++;
            var content = reads == 1 ? original : observedTransform?.Invoke(written) ?? written;
            return CalendarResourceRead.Success(
                href,
                reads == 1 ? "\"r1\"" : observedEntityTag,
                Encoding.UTF8.GetBytes(content));
        });
        client.UpdateCalendarResourceAsync(Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                written = Encoding.UTF8.GetString(call.Arg<CalendarResourceUpdateRequest>().AuthoritativeUtf8.Span);
                return new(dispatchCode);
            });
        outbound = () => written;
        return client;
    }

    private static CalendarEventPatchRequest EventRequest(CalendarEventPatch patch) => new(
        new(EventHref, "matrix", CalendarEntityKind.Event, "\"r1\""),
        new("master"),
        patch);

    private static CalendarService Service(ICalendarClient client, TimeProvider? timeProvider = null) => new(
        client,
        Options.Create(new CalDavOptions { BaseUrl = "https://cal.example/", Username = "u", Password = "p" }),
        Substitute.For<ILogger<CalendarService>>(),
        timeProvider ?? new FrozenTimeProvider(),
        Substitute.For<ICalendarEntityIdentityGenerator>());

    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class ControllableTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }

        public void FireTimers()
        {
            foreach (var timer in _timers)
                timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            public void Fire() => callback(state);
        }
    }
}
