using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarEntityPatchServiceTests
{
    [Fact]
    public async Task PatchEventAsync_ChangesOnlySummaryAndLastModifiedWithExactReviewedRevision()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nCREATED:20260815T100000Z\r\nLAST-MODIFIED:20260815T100000Z\r\nSEQUENCE:7\r\nSUMMARY:Original\r\nX-KEEP;X-DUP=One,one,TWO:https://example.test/a,b;c\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        const string expected = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nCREATED:20260815T100000Z\r\nLAST-MODIFIED:20260817T120000Z\r\nSEQUENCE:7\r\nSUMMARY:Updated\r\nX-KEEP;X-DUP=One,one,TWO:https://example.test/a,b;c\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
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
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            Read(resourceHref, "\"r1\"", original),
            Read(resourceHref, "\"r2\"", expected));
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        var sut = CreateService(client);

        var result = await sut.PatchEventAsync(
            new CalendarEventPatchRequest(
                new CalendarResourceRevisionReference(resourceHref, "event-1", CalendarEntityKind.Event, "\"r1\""),
                new CalendarMutationTarget("master"),
                new CalendarEventPatch(new CalendarScalarPatch<string>(CalendarScalarPatchOperation.Set, "Updated"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        result.Snapshot!.EntityTag.ShouldBe("\"r2\"");
        Encoding.UTF8.GetString(dispatched.Span).ShouldBe(expected);
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Is<CalendarResourceUpdateRequest>(request => request.EntityTag == "\"r1\""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_ReturnsNoChangeWithoutWriting()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nSUMMARY:Same\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, content);
        var sut = CreateService(client);

        var result = await sut.PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Same"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.NoChange);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_RejectsAmbiguousCategoryRemovalWithoutWriting()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nCATEGORIES:Work,Home\r\nCATEGORIES:Work\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, content);
        var sut = CreateService(client);

        var result = await sut.PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Categories: new(
                CalendarCollectionPatchOperation.AddRemove,
                Remove: ["Work"]))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.RemovalAmbiguous);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, CalendarEntityPatchCode.RemovalNotFound)]
    [InlineData(true, CalendarEntityPatchCode.RemovalAmbiguous)]
    public async Task PatchEventAsync_ReturnsTypedStructuredRemovalFailureWithoutWriting(
        bool duplicateIntent,
        CalendarEntityPatchCode expectedCode)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nATTENDEE:mailto:present@example.test\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var requested = new CalendarAttendee("mailto:missing@example.test", []);
        IReadOnlyList<CalendarAttendee> removals = duplicateIntent ? [requested, requested] : [requested];
        var client = ClientReturning(href, content);
        var patch = new CalendarCollectionPatch<CalendarAttendee>(
            CalendarCollectionPatchOperation.AddRemove,
            Remove: removals,
            Field: CalendarCollectionField.Attendees);

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Collections: [patch])), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_AddsCategoryWithoutRewritingExistingOccurrences()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nCATEGORIES;LANGUAGE=en:Work\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        const string expected = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nCATEGORIES;LANGUAGE=en:Work\r\nX-KEEP:opaque\r\nCATEGORIES:Home\r\nLAST-MODIFIED:20260817T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(
            Read(href, "\"r1\"", original), Read(href, "\"r2\"", expected));
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        var sut = CreateService(client);

        var result = await sut.PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Categories: new(
                CalendarCollectionPatchOperation.AddRemove,
                Add: ["Home"]))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        Encoding.UTF8.GetString(dispatched.Span).ShouldBe(expected);
    }

    [Fact]
    public async Task PatchEventAsync_AddsTypedAttendeeWithoutRewritingUnknownSlices()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nX-KEEP;P=One,one:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        const string expected = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nX-KEEP;P=One,one:opaque\r\nATTENDEE:mailto:person@example.com\r\nLAST-MODIFIED:20260817T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(
            Read(href, "\"r1\"", original), Read(href, "\"r2\"", expected));
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        var patch = new CalendarCollectionPatch<CalendarAttendee>(
            CalendarCollectionPatchOperation.AddRemove,
            Add: [new CalendarAttendee("mailto:person@example.com", [])],
            Field: CalendarCollectionField.Attendees);

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Collections: [patch])), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        Encoding.UTF8.GetString(dispatched.Span).ShouldBe(expected);
    }

    [Fact]
    public async Task PatchEventAsync_PreservesUnaddressedScalarParametersAndReplacesOnlyTemporalValueParameters()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART;X-FIRST=1;TZID=America/New_York;X-LAST=2:20260820T100000\r\nSUMMARY;LANGUAGE=pt;X-KEEP=one;X-KEEP=two:Original\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(
                Summary: new(CalendarScalarPatchOperation.Set, "Updated"),
                Start: new(CalendarScalarPatchOperation.Set, new CalendarTemporalValue(
                    CalendarTemporalKind.ZonedDateTime,
                    "2026-08-21T11:00:00",
                    "Europe/London")))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("SUMMARY;LANGUAGE=pt;X-KEEP=one;X-KEEP=two:Updated\r\n");
        outbound.ShouldContain("DTSTART;X-FIRST=1;TZID=Europe/London;X-LAST=2:20260821T110000\r\n");
    }

    [Fact]
    public async Task PatchEventAsync_ExistingOrganizerSetReplacesTheCompleteStructuredValue()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nORGANIZER;CN=Original;X-KEEP=one;X-KEEP=two:mailto:old@example.test\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));
        var requested = new CalendarNamedUri(
            "mailto:new@example.test",
            "Requested",
            [new CalendarParameter("X-NEW", ["ignored-for-existing"])]);

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Organizer: new(CalendarScalarPatchOperation.Set, requested))),
            CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("ORGANIZER;CN=Requested;X-NEW=ignored-for-existing:mailto:new@example.test\r\n");
        outbound.ShouldNotContain("CN=Original");
        outbound.ShouldNotContain("X-KEEP");
    }

    [Theory]
    [InlineData(CalendarScalarPatchOperation.Set)]
    [InlineData(CalendarScalarPatchOperation.Clear)]
    public async Task PatchEventAsync_RejectsAnyMutationOfDerivedScalarWithoutWriting(
        CalendarScalarPatchOperation operation)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nSUMMARY;DERIVED=TRUE;LANGUAGE=pt:Computed\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, content);
        client.UpdateCalendarResourceAsync(Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        var scalar = operation == CalendarScalarPatchOperation.Set
            ? new CalendarScalarPatch<string>(operation, "Changed")
            : new CalendarScalarPatch<string>(operation);

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Summary: scalar)), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_RejectsOrganizerSetThatIntroducesDerivedDataWithoutWriting()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, content);
        var organizer = new CalendarNamedUri(
            "mailto:computed@example.test",
            "Computed",
            [new CalendarParameter("DERIVED", ["TRUE"])]);

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Organizer: new(CalendarScalarPatchOperation.Set, organizer))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_PreservesDerivedLastModifiedOccurrenceByteExactly()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nLAST-MODIFIED;DERIVED=TRUE;X-KEEP=one,two:20260815T100000Z\r\nSUMMARY:Before\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "After"))),
            CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("LAST-MODIFIED;DERIVED=TRUE;X-KEEP=one,two:20260815T100000Z\r\n");
        outbound.Split("LAST-MODIFIED", StringSplitOptions.None).Length.ShouldBe(2);
    }

    [Theory]
    [InlineData("DTSTAMP:20260816T100000Z", "DTSTAMP:20260816T100001Z")]
    [InlineData("CREATED:20260815T100000Z", "CREATED:20260815T100001Z")]
    [InlineData("SEQUENCE:7", "SEQUENCE:8")]
    public async Task PatchEventAsync_PostWriteVerificationRejectsDriftInPreservedFields(
        string preserved,
        string drifted)
    {
        const string href = "https://cal.example/events/event-1.ics";
        var original = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nCREATED:20260815T100000Z\r\nSEQUENCE:7\r\nDTSTART:20260820T100000Z\r\nSUMMARY:Original\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var written = string.Empty;
        client.UpdateCalendarResourceAsync(Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                written = Encoding.UTF8.GetString(call.Arg<CalendarResourceUpdateRequest>().AuthoritativeUtf8.Span);
                return new(CalendarResourceUpdateDispatchCode.Dispatched);
        });
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(
            _ => Read(href, "\"r1\"", original),
            _ => Read(href, "\"r2\"", written.Replace(preserved, drifted, StringComparison.Ordinal)));

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Updated"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.FidelityFailure);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
    }

    [Fact]
    public async Task PatchEventAsync_SemanticallyMatchesFoldedAndReorderedAttendeeForLosslessRemoval()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nattendee;role=req-participant;\r\n cn=Person:mailto:person@example.com\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));
        var remove = new CalendarCollectionPatch<CalendarAttendee>(
            CalendarCollectionPatchOperation.AddRemove,
            Remove: [new CalendarAttendee(
                "mailto:person@example.com",
                [],
                CommonName: "Person",
                Role: "required")],
            Field: CalendarCollectionField.Attendees);

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Collections: [remove])), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldNotContain("mailto:person@example.com");
        outbound.ShouldContain("X-KEEP:opaque\r\n");
    }

    [Fact]
    public async Task PatchEventAsync_SemanticallyMatchesComponentAndRemovesItsWholeLosslessOccurrence()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nBEGIN:PARTICIPANT\r\nX-UNMODELED;P=one,two:preserve-until-whole-removal\r\nparticipant-type:active\r\nuid:participant-1\r\nEND:PARTICIPANT\r\nBEGIN:PARTICIPANT\r\nUID:keep\r\nPARTICIPANT-TYPE:ACTIVE\r\nEND:PARTICIPANT\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));
        var participant = new CalendarParticipant(
            new CalendarTextValue("participant-1", []),
            new CalendarTextValue("ACTIVE", []));
        var remove = new CalendarCollectionPatch<CalendarParticipant>(
            CalendarCollectionPatchOperation.AddRemove,
            Remove: [participant],
            Field: CalendarCollectionField.Participants);

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Collections: [remove])), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldNotContain("participant-1");
        outbound.ShouldNotContain("preserve-until-whole-removal");
        outbound.ShouldContain("UID:keep\r\n");
    }

    [Fact]
    public async Task PatchEventAsync_OrdinaryRecurringMasterEditPreservesDefinitionAndOverrideBytes()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nSUMMARY;X-KEEP=one,two:Override\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Changed master"))),
            CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("RRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Changed master\r\n");
        outbound.ShouldContain("DTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nSUMMARY;X-KEEP=one,two:Override\r\n");
    }

    [Fact]
    public async Task PatchEventAsync_RejectsRecurringMasterStartMutationThatWouldChangeMembership()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Start: new(
                CalendarScalarPatchOperation.Set,
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PatchEventAsync_RejectsMutationOfDerivedCollectionOccurrenceWithoutWriting(bool categories)
    {
        const string href = "https://cal.example/events/event-1.ics";
        var occurrence = categories
            ? "CATEGORIES;DERIVED=TRUE:Work\r\n"
            : "ATTENDEE;DERIVED=TRUE:mailto:person@example.test\r\n";
        var original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\n"
            + occurrence + "END:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var patch = categories
            ? new CalendarEventPatch(Categories: new(
                CalendarCollectionPatchOperation.AddRemove,
                Remove: ["Work"]))
            : new CalendarEventPatch(Collections: [new CalendarCollectionPatch<CalendarAttendee>(
                CalendarCollectionPatchOperation.AddRemove,
                Remove: [new CalendarAttendee(
                    "mailto:person@example.test",
                    [new CalendarParameter("DERIVED", ["TRUE"])])],
                Field: CalendarCollectionField.Attendees)]);

        var result = await CreateService(client).PatchEventAsync(EventRequest(href, patch), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PatchEventAsync_RejectsReplaceAllThatWouldRemoveDerivedOccurrence(bool categories)
    {
        const string href = "https://cal.example/events/event-1.ics";
        var occurrence = categories
            ? "CATEGORIES;DERIVED=TRUE:Work\r\n"
            : "ATTENDEE;DERIVED=TRUE:mailto:person@example.test\r\n";
        var original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\n"
            + occurrence + "END:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var patch = categories
            ? new CalendarEventPatch(Categories: new(CalendarCollectionPatchOperation.ReplaceAll, Values: []))
            : new CalendarEventPatch(Collections: [new CalendarCollectionPatch<CalendarAttendee>(
                CalendarCollectionPatchOperation.ReplaceAll,
                Values: [],
                Field: CalendarCollectionField.Attendees)]);

        var result = await CreateService(client).PatchEventAsync(EventRequest(href, patch), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_RejectsReplaceAllThatIntroducesDerivedOccurrenceWithoutWriting()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nATTENDEE:mailto:existing@example.test\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, content);
        var replacement = new CalendarAttendee(
            "mailto:computed@example.test",
            [new CalendarParameter("DERIVED", ["TRUE"])]);
        var patch = new CalendarCollectionPatch<CalendarAttendee>(
            CalendarCollectionPatchOperation.ReplaceAll,
            Values: [replacement],
            Field: CalendarCollectionField.Attendees);

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Collections: [patch])), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_AddOnlyPreservesExistingDerivedCollectionOccurrence()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nCATEGORIES;DERIVED=TRUE:Computed\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Categories: new(
                CalendarCollectionPatchOperation.AddRemove,
                Add: ["Manual"]))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("CATEGORIES;DERIVED=TRUE:Computed\r\n");
        outbound.ShouldContain("CATEGORIES:Manual\r\n");
    }

    [Fact]
    public async Task PatchEventAsync_DefiniteDispatchWithoutVerificationBytesIsCommittedButUnverified()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nSUMMARY:Original\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(
            _ => Read(href, "\"r1\"", original),
            _ => new CalendarResourceRead(CalendarResourceReadCode.ConcurrencyUnavailable));
        client.UpdateCalendarResourceAsync(Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Updated"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.CommittedButUnverified);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
    }

    [Theory]
    [InlineData(CalendarResourceUpdateDispatchCode.Dispatched, true, CalendarEntityPatchCode.CommittedButConcurrencyUnavailable, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceUpdateDispatchCode.Dispatched, false, CalendarEntityPatchCode.FidelityFailure, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceUpdateDispatchCode.PossiblyDispatched, true, CalendarEntityPatchCode.CommittedButConcurrencyUnavailable, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceUpdateDispatchCode.PossiblyDispatched, false, CalendarEntityPatchCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task PatchEventAsync_UsesUnversionedVerificationBytesOnlyAsSemanticCommitEvidence(
        CalendarResourceUpdateDispatchCode dispatchCode,
        bool matchesIntended,
        CalendarEntityPatchCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nSUMMARY:Original\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        ReadOnlyMemory<byte> written = default;
        var client = ClientReturning(href, original);
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => written = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(dispatchCode));
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(
            _ => Read(href, "\"r1\"", original),
            _ => new CalendarResourceRead(
                CalendarResourceReadCode.ConcurrencyUnavailable,
                href,
                AuthoritativeUtf8: matchesIntended ? written : Encoding.UTF8.GetBytes(original)));

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Updated"))), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        result.Phase.ShouldBe(CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation);
        result.Snapshot.ShouldBeNull();
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversize")]
    public async Task PatchEventAsync_DefiniteDispatchWithUnusableUnversionedBytesIsUnverified(string mode)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nSUMMARY:Original\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var bytes = mode == "malformed"
            ? Encoding.UTF8.GetBytes("not iCalendar")
            : new byte[(4 * 1024 * 1024) + 1];
        var client = ClientReturning(href, original);
        client.UpdateCalendarResourceAsync(Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(
            _ => Read(href, "\"r1\"", original),
            _ => new CalendarResourceRead(CalendarResourceReadCode.ConcurrencyUnavailable, href, AuthoritativeUtf8: bytes));

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Updated"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.CommittedButUnverified);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Phase.ShouldBe(CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation);
        result.Snapshot.ShouldBeNull();
    }

    [Fact]
    public async Task ReviewEventPatchAsync_PerformsCompleteDryRunAndReturnsStableIntentWithoutWriting()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nSUMMARY:Original\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var service = CreateService(client);

        var ready = await service.ReviewEventPatchAsync(EventRequest(
            href,
            new CalendarEventPatch(Categories: new(
                CalendarCollectionPatchOperation.ReplaceAll,
                Values: ["Work"]))), CancellationToken.None);
        var noChange = await service.ReviewEventPatchAsync(EventRequest(
            href,
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Original"))), CancellationToken.None);

        ready.Outcome.ShouldBeNull();
        ready.IntentDigest.Length.ShouldBe(32);
        noChange.Outcome!.Code.ShouldBe(CalendarEntityPatchCode.NoChange);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewEventPatchAsync_RejectsSnapshotAboveFourMiBBeforeElicitationOrWrite()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string prefix = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nX-LARGE:";
        const string suffix = "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var original = prefix + new string('x', (4 * 1024 * 1024) + 1 - prefix.Length - suffix.Length) + suffix;
        var client = ClientReturning(href, original);

        var review = await CreateService(client).ReviewEventPatchAsync(EventRequest(
            href,
            new CalendarEventPatch(Categories: new(
                CalendarCollectionPatchOperation.ReplaceAll,
                Values: ["Work"]))), CancellationToken.None);

        review.Outcome!.Code.ShouldBe(CalendarEntityPatchCode.PayloadTooLarge);
        review.Outcome.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("A", false)]
    [InlineData("AA", true)]
    public async Task ReviewEventPatchAsync_EnforcesFourMiBLimitOnTheFinalEditedBody(
        string summary,
        bool exceedsLimit)
    {
        const int maximumBytes = 4 * 1024 * 1024;
        const string href = "https://cal.example/events/event-1.ics";
        const string prefix = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nX-LARGE:";
        const string suffix = "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        const string exactAdded = "SUMMARY:A\r\nLAST-MODIFIED:20260817T120000Z\r\n";
        var fillerLength = maximumBytes - prefix.Length - suffix.Length - exactAdded.Length;
        var original = prefix + new string('x', fillerLength) + suffix;
        var client = ClientReturning(href, original);

        var review = await CreateService(client).ReviewEventPatchAsync(EventRequest(
            href,
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, summary))),
            CancellationToken.None);

        if (exceedsLimit)
        {
            review.Outcome!.Code.ShouldBe(CalendarEntityPatchCode.PayloadTooLarge);
            review.Outcome.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        }
        else
        {
            review.Outcome.ShouldBeNull();
            review.IntentDigest.Length.ShouldBe(32);
        }
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    private static CalendarService CreateService(ICalendarClient client) => new(
        client,
        Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example/",
            Username = "user",
            Password = "pass"
        }),
        Substitute.For<ILogger<CalendarService>>(),
        new FrozenTimeProvider(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero)),
        Substitute.For<ICalendarEntityIdentityGenerator>());

    private static ICalendarClient ClientReturning(string href, string content)
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
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(Read(href, "\"r1\"", content));
        return client;
    }

    private static CalendarEventPatchRequest EventRequest(string href, CalendarEventPatch patch) => new(
        new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
        new CalendarMutationTarget("master"),
        patch);

    private static CalendarResourceRead Read(string href, string tag, string content) =>
        CalendarResourceRead.Success(href, tag, Encoding.UTF8.GetBytes(content));

    private static string Diagnostic(CalendarEntityPatchResult result) => result.Code + ":" + string.Join(
        ',',
        result.Snapshot?.Diagnostics.Select(item => item.Code) ?? []);

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
