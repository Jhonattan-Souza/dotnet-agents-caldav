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

public sealed class CalendarOccurrenceMutationServiceTests
{
    private const string Href = "https://cal.example/events/series.ics";

    [Fact]
    public async Task AddOccurrenceAsync_AppendsOneExplicitRdateWithExactIfMatchAndVerifiedReadback()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nX-KEEP;P=One,one:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        const string expected = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nX-KEEP;P=One,one:opaque\r\nRDATE:20260825T090000Z\r\nLAST-MODIFIED:20260817T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original, expected);
        CalendarResourceUpdateRequest? dispatched = null;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));

        var result = await CreateService(client).AddOccurrenceAsync(
            Request(new(CalendarTemporalKind.UtcDateTime, "2026-08-25T09:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot!.EntityTag.ShouldBe("\"r2\"");
        dispatched.ShouldNotBeNull();
        dispatched.EntityTag.ShouldBe("\"r1\"");
        Encoding.UTF8.GetString(dispatched.AuthoritativeUtf8.Span).ShouldBe(expected);
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExcludeOccurrenceAsync_AddsExactExdateWithoutRemovingAnExistingOverride()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260819T090000Z\r\nDTSTART:20260819T110000Z\r\nSUMMARY:Moved\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        const string expected = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEXDATE:20260819T090000Z\r\nLAST-MODIFIED:20260817T120000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260819T090000Z\r\nDTSTART:20260819T110000Z\r\nSUMMARY:Moved\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original, expected);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));

        var result = await CreateService(client).ExcludeOccurrenceAsync(
            Request(new(CalendarTemporalKind.UtcDateTime, "2026-08-19T09:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        Encoding.UTF8.GetString(dispatched.Span).ShouldBe(expected);
        Encoding.UTF8.GetString(dispatched.Span).ShouldContain("SUMMARY:Moved\r\n");
    }

    [Fact]
    public async Task RestoreExclusionAsync_RemovesOnlyTheAddressedExdateValue()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nEXDATE;X-KEEP=opaque:20260819T090000Z,20260820T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        const string expected = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nEXDATE;X-KEEP=opaque:20260820T090000Z\r\nLAST-MODIFIED:20260817T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original, expected);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));

        var result = await CreateService(client).RestoreOccurrenceExclusionAsync(
            Request(new(CalendarTemporalKind.UtcDateTime, "2026-08-19T09:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        Encoding.UTF8.GetString(dispatched.Span).ShouldBe(expected);
    }

    [Fact]
    public async Task CancelOccurrenceAsync_MaterializesACompleteIndividualFromNearestRange()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T110000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260819T090000Z\r\nDTSTART:20260819T110000Z\r\nSUMMARY:Range\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            _ => Read("\"r1\"", original),
            _ => Read("\"r2\"", Encoding.UTF8.GetString(dispatched.Span)));

        var result = await CreateService(client).CancelOccurrenceAsync(
            Request(new(CalendarTemporalKind.UtcDateTime, "2026-08-20T09:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("RECURRENCE-ID:20260820T090000Z\r\n");
        outbound.ShouldContain("DTSTART:20260820T110000Z\r\n");
        outbound.ShouldContain("SUMMARY:Range\r\n");
        outbound.ShouldContain("X-KEEP:opaque\r\n");
        outbound.ShouldContain("STATUS:CANCELLED\r\n");
        outbound.Split("BEGIN:VEVENT", StringSplitOptions.None).Length.ShouldBe(4);
    }

    [Fact]
    public async Task RestoreCancellationAsync_ClearsOnlyStatusWhileExdateStillSuppressesTheOverride()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEXDATE:20260819T090000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T110000Z\r\nRECURRENCE-ID:20260819T090000Z\r\nDTSTART:20260819T120000Z\r\nSTATUS:CANCELLED\r\nSUMMARY:Preserved\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            _ => Read("\"r1\"", original),
            _ => Read("\"r2\"", Encoding.UTF8.GetString(dispatched.Span)));

        var result = await CreateService(client).RestoreOccurrenceCancellationAsync(
            Request(new(CalendarTemporalKind.UtcDateTime, "2026-08-19T09:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("EXDATE:20260819T090000Z\r\n");
        outbound.ShouldContain("RECURRENCE-ID:20260819T090000Z\r\n");
        outbound.ShouldContain("SUMMARY:Preserved\r\n");
        outbound.ShouldContain("X-KEEP:opaque\r\n");
        outbound.ShouldNotContain("STATUS:CANCELLED\r\n");
    }

    [Theory]
    [InlineData("base", "add", CalendarEntityPatchCode.NoChange)]
    [InlineData("excluded", "add", CalendarEntityPatchCode.InvalidInput)]
    [InlineData("excluded", "exclude", CalendarEntityPatchCode.NoChange)]
    [InlineData("base", "restore_exclusion", CalendarEntityPatchCode.NoChange)]
    [InlineData("base", "restore_exclusion_missing", CalendarEntityPatchCode.NotFound)]
    [InlineData("base", "exclude_missing", CalendarEntityPatchCode.NotFound)]
    [InlineData("cancelled", "cancel", CalendarEntityPatchCode.NoChange)]
    [InlineData("base", "restore_cancellation", CalendarEntityPatchCode.NoChange)]
    [InlineData("base", "restore_cancellation_missing", CalendarEntityPatchCode.NotFound)]
    [InlineData("base", "cancel_missing", CalendarEntityPatchCode.NotFound)]
    public async Task MembershipMutation_DeterministicNoWriteOutcomes(
        string state,
        string operation,
        CalendarEntityPatchCode expectedCode)
    {
        var client = ClientReturning(EventFixture(state));

        var result = await ExecuteAsync(CreateService(client), operation);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("add")]
    [InlineData("exclude")]
    [InlineData("restore_exclusion")]
    [InlineData("cancel")]
    [InlineData("restore_cancellation")]
    public async Task MembershipMutation_MultipleRrulesAreUnevaluableBeforeWrite(string operation)
    {
        var original = EventFixture("base").Replace(
            "RRULE:FREQ=DAILY;COUNT=2\r\n",
            "RRULE:FREQ=DAILY;COUNT=2\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\n",
            StringComparison.Ordinal);
        var client = ClientReturning(original);

        var result = await ExecuteAsync(CreateService(client), operation);

        result.Code.ShouldBe(CalendarEntityPatchCode.RecurrenceUnevaluable);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelOccurrenceAsync_DuplicateIndividualIdentityIsOpaqueWithoutWrite()
    {
        const string duplicate = "BEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T110000Z\r\nRECURRENCE-ID:20260819T090000Z\r\nDTSTART:20260819T110000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T120000Z\r\nRECURRENCE-ID:20260819T090000Z\r\nDTSTART:20260819T120000Z\r\nEND:VEVENT\r\n";
        var original = EventFixture("base").Replace(
            "END:VCALENDAR\r\n",
            duplicate + "END:VCALENDAR\r\n",
            StringComparison.Ordinal);
        var client = ClientReturning(original);

        var result = await CreateService(client).CancelOccurrenceAsync(
            Request(new(CalendarTemporalKind.UtcDateTime, "2026-08-19T09:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.OpaqueResource);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelOccurrenceAsync_UnknownNamedZoneIsTemporallyUnresolvedBeforeWrite()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART;TZID=Private/Unknown:20260818T090000\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        var request = Request(new(
            CalendarTemporalKind.ZonedDateTime,
            "2026-08-19T09:00:00",
            "Private/Unknown"));

        var result = await CreateService(client).CancelOccurrenceAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.TemporalUnresolved);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarEntityKind.Event, "VEVENT", "DTEND")]
    [InlineData(CalendarEntityKind.Todo, "VTODO", "DUE")]
    public async Task CancelOccurrenceAsync_UnresolvedInheritedEndpointIsTemporallyUnresolvedBeforeWrite(
        CalendarEntityKind kind,
        string component,
        string endpoint)
    {
        var original = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:{component}\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART;TZID=Europe/Zurich:20260818T090000\r\n{endpoint};TZID=Private/Unknown:20260818T100000\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:{component}\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        var request = new CalendarOccurrenceMutationRequest(
            new CalendarResourceRevisionReference(Href, "series-1", kind, "\"r1\""),
            new CalendarTemporalValue(
                CalendarTemporalKind.ZonedDateTime,
                "2026-08-19T09:00:00",
                "Europe/Zurich"));

        var result = await CreateService(client).CancelOccurrenceAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.TemporalUnresolved);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarEntityKind.Event, "VEVENT", "DTEND:20260818T090000Z")]
    [InlineData(CalendarEntityKind.Todo, "VTODO", "DUE:20260818T080000Z")]
    public async Task CancelOccurrenceAsync_NonpositiveInheritedSpanIsInvalidBeforeWrite(
        CalendarEntityKind kind,
        string component,
        string endpoint)
    {
        var original = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:{component}\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\n{endpoint}\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:{component}\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        var request = new CalendarOccurrenceMutationRequest(
            new CalendarResourceRevisionReference(Href, "series-1", kind, "\"r1\""),
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-19T09:00:00Z"));

        var result = await CreateService(client).CancelOccurrenceAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidCalendarData);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddOccurrenceAsync_RdatePeriodIsUnsupportedBeforeWrite()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nRDATE;VALUE=PERIOD:20260819T090000Z/PT1H\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);

        var result = await CreateService(client).AddOccurrenceAsync(
            Request(new(CalendarTemporalKind.UtcDateTime, "2026-08-20T09:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.UnsupportedCapability);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarEntityKind.Event, "VEVENT", "DTSTART;VALUE=DATE:20260818", CalendarTemporalKind.Date, "2026-08-25", null, "RDATE;VALUE=DATE:20260825")]
    [InlineData(CalendarEntityKind.Todo, "VTODO", "DTSTART;VALUE=DATE:20260818", CalendarTemporalKind.Date, "2026-08-25", null, "RDATE;VALUE=DATE:20260825")]
    [InlineData(CalendarEntityKind.Event, "VEVENT", "DTSTART:20260818T090000", CalendarTemporalKind.FloatingDateTime, "2026-08-25T09:00:00", null, "RDATE:20260825T090000")]
    [InlineData(CalendarEntityKind.Todo, "VTODO", "DTSTART:20260818T090000", CalendarTemporalKind.FloatingDateTime, "2026-08-25T09:00:00", null, "RDATE:20260825T090000")]
    [InlineData(CalendarEntityKind.Event, "VEVENT", "DTSTART:20260818T090000Z", CalendarTemporalKind.UtcDateTime, "2026-08-25T09:00:00Z", null, "RDATE:20260825T090000Z")]
    [InlineData(CalendarEntityKind.Todo, "VTODO", "DTSTART:20260818T090000Z", CalendarTemporalKind.UtcDateTime, "2026-08-25T09:00:00Z", null, "RDATE:20260825T090000Z")]
    [InlineData(CalendarEntityKind.Event, "VEVENT", "DTSTART;TZID=Europe/Zurich:20260818T090000", CalendarTemporalKind.ZonedDateTime, "2026-08-25T09:00:00", "Europe/Zurich", "RDATE;TZID=Europe/Zurich:20260825T090000")]
    [InlineData(CalendarEntityKind.Todo, "VTODO", "DTSTART;TZID=Europe/Zurich:20260818T090000", CalendarTemporalKind.ZonedDateTime, "2026-08-25T09:00:00", "Europe/Zurich", "RDATE;TZID=Europe/Zurich:20260825T090000")]
    public async Task AddOccurrenceAsync_PreservesEveryTemporalFamilyForEventAndTodo(
        CalendarEntityKind kind,
        string component,
        string start,
        CalendarTemporalKind temporalKind,
        string value,
        string? timeZoneId,
        string expectedRdate)
    {
        var original = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:{component}\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\n{start}\r\nEND:{component}\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            _ => Read("\"r1\"", original),
            _ => Read("\"r2\"", Encoding.UTF8.GetString(dispatched.Span)));
        var request = new CalendarOccurrenceMutationRequest(
            new CalendarResourceRevisionReference(Href, "series-1", kind, "\"r1\""),
            new CalendarTemporalValue(temporalKind, value, timeZoneId));

        var result = await CreateService(client).AddOccurrenceAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        Encoding.UTF8.GetString(dispatched.Span).ShouldContain(expectedRdate + "\r\n");
    }

    [Fact]
    public async Task EventMembership_CancelThenExcludeRestoresEachStateIndependently()
    {
        var content = EventFixture("base");
        var revisionNumber = 1;
        var entityTag = "\"r1\"";
        var updateCount = 0;
        var client = ClientReturning(content);
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(_ => Read(entityTag, content));
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<CalendarResourceUpdateRequest>();
                request.EntityTag.ShouldBe(entityTag);
                content = Encoding.UTF8.GetString(request.AuthoritativeUtf8.Span);
                entityTag = $"\"r{++revisionNumber}\"";
                updateCount++;
                return new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched);
            });
        var service = CreateService(client);
        var add = await service.AddOccurrenceAsync(StatefulRequest(
            entityTag,
            "2026-08-20T09:00:00Z"), CancellationToken.None);
        var cancel = await service.CancelOccurrenceAsync(StatefulRequest(
            add.Snapshot!.EntityTag,
            "2026-08-19T09:00:00Z"), CancellationToken.None);
        var exclude = await service.ExcludeOccurrenceAsync(StatefulRequest(
            cancel.Snapshot!.EntityTag,
            "2026-08-19T09:00:00Z"), CancellationToken.None);

        var restoreExclusion = await service.RestoreOccurrenceExclusionAsync(StatefulRequest(
            exclude.Snapshot!.EntityTag,
            "2026-08-19T09:00:00Z"), CancellationToken.None);

        restoreExclusion.Code.ShouldBe(CalendarEntityPatchCode.Success);
        content.ShouldNotContain("EXDATE:20260819T090000Z");
        content.ShouldContain("STATUS:CANCELLED\r\n");
        var restoreCancellation = await service.RestoreOccurrenceCancellationAsync(StatefulRequest(
            restoreExclusion.Snapshot!.EntityTag,
            "2026-08-19T09:00:00Z"), CancellationToken.None);
        restoreCancellation.Code.ShouldBe(CalendarEntityPatchCode.Success);
        content.ShouldContain("RDATE:20260820T090000Z\r\n");
        content.ShouldContain("RECURRENCE-ID:20260819T090000Z\r\n");
        content.ShouldNotContain("STATUS:CANCELLED");
        updateCount.ShouldBe(5);
    }

    [Fact]
    public async Task CancelOccurrenceAsync_TodoMaterializesCompleteOverrideWithDue()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nDUE:20260818T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Todo series\r\nX-KEEP:opaque\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            _ => Read("\"r1\"", original),
            _ => Read("\"r2\"", Encoding.UTF8.GetString(dispatched.Span)));
        var request = new CalendarOccurrenceMutationRequest(
            new CalendarResourceRevisionReference(Href, "series-1", CalendarEntityKind.Todo, "\"r1\""),
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-19T09:00:00Z"));

        var result = await CreateService(client).CancelOccurrenceAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("BEGIN:VTODO\r\n");
        outbound.ShouldContain("RECURRENCE-ID:20260819T090000Z\r\n");
        outbound.ShouldContain("DTSTART:20260819T090000Z\r\n");
        outbound.ShouldContain("DUE:20260819T100000Z\r\n");
        outbound.ShouldContain("SUMMARY:Todo series\r\n");
        outbound.ShouldContain("X-KEEP:opaque\r\n");
        outbound.ShouldContain("STATUS:CANCELLED\r\n");
    }

    [Fact]
    public async Task CompleteTodoAsync_NonRecurringUsesOneInjectedInstantForEveryCompletionEffect()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nSUMMARY:Preserved\r\nX-KEEP:opaque\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        const string expected = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nSUMMARY:Preserved\r\nX-KEEP:opaque\r\nSTATUS:COMPLETED\r\nPERCENT-COMPLETE:100\r\nCOMPLETED:20260817T120000Z\r\nLAST-MODIFIED:20260817T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original, expected);
        CalendarResourceUpdateRequest? dispatched = null;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        var request = new CalendarTodoCompletionRequest(
            new CalendarResourceRevisionReference(Href, "series-1", CalendarEntityKind.Todo, "\"r1\""));

        var result = await CreateService(client).CompleteTodoAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        dispatched.ShouldNotBeNull();
        dispatched.EntityTag.ShouldBe("\"r1\"");
        Encoding.UTF8.GetString(dispatched.AuthoritativeUtf8.Span).ShouldBe(expected);
    }

    [Fact]
    public async Task CompleteTodoAsync_ReadsInjectedClockOnceForCoordinatedFields()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            _ => Read("\"r1\"", original),
            _ => Read("\"r2\"", Encoding.UTF8.GetString(dispatched.Span)));
        var timeProvider = new AdvancingTimeProvider(
            new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        var request = new CalendarTodoCompletionRequest(
            new CalendarResourceRevisionReference(Href, "series-1", CalendarEntityKind.Todo, "\"r1\""));

        var result = await CreateService(client, timeProvider).CompleteTodoAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        timeProvider.UtcNowReadCount.ShouldBe(1);
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("COMPLETED:20260817T120000Z\r\n");
        outbound.ShouldContain("LAST-MODIFIED:20260817T120000Z\r\n");
    }

    [Fact]
    public async Task CompleteTodoAsync_RecurringCompletesOnlyTheTargetedOriginalIdentity()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nDUE:20260818T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Master\r\nX-KEEP:opaque\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            _ => Read("\"r1\"", original),
            _ => Read("\"r2\"", Encoding.UTF8.GetString(dispatched.Span)));
        var request = new CalendarTodoCompletionRequest(
            new CalendarResourceRevisionReference(Href, "series-1", CalendarEntityKind.Todo, "\"r1\""),
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-19T09:00:00Z"));

        var result = await CreateService(client).CompleteTodoAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.Split("BEGIN:VTODO", StringSplitOptions.None).Length.ShouldBe(3);
        outbound.ShouldContain("RECURRENCE-ID:20260819T090000Z\r\n");
        outbound.ShouldContain("DTSTART:20260819T090000Z\r\n");
        outbound.ShouldContain("DUE:20260819T100000Z\r\n");
        outbound.ShouldContain("SUMMARY:Master\r\n");
        outbound.ShouldContain("X-KEEP:opaque\r\n");
        outbound.Split("STATUS:COMPLETED", StringSplitOptions.None).Length.ShouldBe(2);
        outbound.Split("PERCENT-COMPLETE:100", StringSplitOptions.None).Length.ShouldBe(2);
        outbound.Split("COMPLETED:20260817T120000Z", StringSplitOptions.None).Length.ShouldBe(2);
        outbound.ShouldNotContain("RECURRENCE-ID:20260820T090000Z");
    }

    [Fact]
    public async Task CompleteTodoAsync_MaterializesFromEffectiveRangeAndPreservesLaterOverrides()
    {
        const string laterRange = "BEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T120000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260821T090000Z\r\nDTSTART:20260821T140000Z\r\nDUE:20260821T150000Z\r\nSUMMARY:Later range\r\nX-LATER:opaque\r\nEND:VTODO\r\n";
        const string laterIndividual = "BEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T130000Z\r\nRECURRENCE-ID:20260822T090000Z\r\nDTSTART:20260822T160000Z\r\nDUE:20260822T170000Z\r\nSUMMARY:Later individual\r\nX-INDIVIDUAL:opaque\r\nEND:VTODO\r\n";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nDUE:20260818T100000Z\r\nRRULE:FREQ=DAILY;COUNT=5\r\nSUMMARY:Master\r\nEND:VTODO\r\nBEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T110000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260819T090000Z\r\nDTSTART:20260819T110000Z\r\nDUE:20260819T120000Z\r\nSUMMARY:Effective range\r\nX-RANGE:opaque\r\nEND:VTODO\r\n" + laterRange + laterIndividual + "END:VCALENDAR\r\n";
        var client = ClientReturning(original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            _ => Read("\"r1\"", original),
            _ => Read("\"r2\"", Encoding.UTF8.GetString(dispatched.Span)));
        var request = new CalendarTodoCompletionRequest(
            new CalendarResourceRevisionReference(Href, "series-1", CalendarEntityKind.Todo, "\"r1\""),
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-20T09:00:00Z"));

        var result = await CreateService(client).CompleteTodoAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("RECURRENCE-ID:20260820T090000Z\r\n");
        outbound.ShouldContain("DTSTART:20260820T110000Z\r\n");
        outbound.ShouldContain("DUE:20260820T120000Z\r\n");
        outbound.ShouldContain("SUMMARY:Effective range\r\n");
        outbound.ShouldContain("X-RANGE:opaque\r\n");
        outbound.ShouldContain(laterRange);
        outbound.ShouldContain(laterIndividual);
        outbound.Split("STATUS:COMPLETED", StringSplitOptions.None).Length.ShouldBe(2);
        outbound.Split("BEGIN:VTODO", StringSplitOptions.None).Length.ShouldBe(6);
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Is<CalendarResourceUpdateRequest>(update => update.EntityTag == "\"r1\""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTodoAsync_CompletesExistingDueOnlyIndividualWithoutInventingStart()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nDUE:20260818T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VTODO\r\nBEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T110000Z\r\nRECURRENCE-ID:20260819T090000Z\r\nDUE:20260819T120000Z\r\nSTATUS:IN-PROCESS\r\nSUMMARY:Moved due-only\r\nX-KEEP:opaque\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            _ => Read("\"r1\"", original),
            _ => Read("\"r2\"", Encoding.UTF8.GetString(dispatched.Span)));
        var request = new CalendarTodoCompletionRequest(
            new CalendarResourceRevisionReference(Href, "series-1", CalendarEntityKind.Todo, "\"r1\""),
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-19T09:00:00Z"));

        var result = await CreateService(client).CompleteTodoAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("RECURRENCE-ID:20260819T090000Z\r\n");
        outbound.ShouldContain("DUE:20260819T120000Z\r\n");
        outbound.ShouldContain("SUMMARY:Moved due-only\r\n");
        outbound.ShouldContain("X-KEEP:opaque\r\n");
        outbound.Split("DTSTART", StringSplitOptions.None).Length.ShouldBe(2);
        outbound.ShouldContain("STATUS:COMPLETED\r\n");
    }

    [Theory]
    [InlineData("recurring_without_identity", CalendarEntityPatchCode.InvalidInput)]
    [InlineData("excluded", CalendarEntityPatchCode.NotFound)]
    [InlineData("cancelled", CalendarEntityPatchCode.InvalidInput)]
    [InlineData("unresolved", CalendarEntityPatchCode.TemporalUnresolved)]
    [InlineData("unevaluable", CalendarEntityPatchCode.RecurrenceUnevaluable)]
    [InlineData("completed", CalendarEntityPatchCode.NoChange)]
    [InlineData("missing", CalendarEntityPatchCode.NotFound)]
    [InlineData("wrong_kind", CalendarEntityPatchCode.EntityKindMismatch)]
    public async Task CompleteTodoAsync_StateInteractionsAreDeterministicAndNeverWrite(
        string state,
        CalendarEntityPatchCode expectedCode)
    {
        var client = ClientReturning(TodoCompletionFixture(state));
        var identity = state switch
        {
            "recurring_without_identity" => null,
            "unresolved" => new CalendarTemporalValue(
                CalendarTemporalKind.ZonedDateTime,
                "2026-08-19T09:00:00",
                "Private/Unknown"),
            "missing" => new CalendarTemporalValue(
                CalendarTemporalKind.UtcDateTime,
                "2026-08-25T09:00:00Z"),
            _ => new CalendarTemporalValue(
                CalendarTemporalKind.UtcDateTime,
                "2026-08-19T09:00:00Z")
        };
        var request = new CalendarTodoCompletionRequest(
            new CalendarResourceRevisionReference(Href, "series-1", CalendarEntityKind.Todo, "\"r1\""),
            identity);

        var result = await CreateService(client).CompleteTodoAsync(request, CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTodoAsync_StaleStrongRevisionReturnsConflictWithoutWrite()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        var request = new CalendarTodoCompletionRequest(
            new CalendarResourceRevisionReference(Href, "series-1", CalendarEntityKind.Todo, "\"stale\""));

        var result = await CreateService(client).CompleteTodoAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTodoAsync_PossiblyDispatchedWriteReconcilesWithoutBlindRetry()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            _ => Read("\"r1\"", original),
            _ => Read("\"r2\"", Encoding.UTF8.GetString(dispatched.Span)));
        var request = new CalendarTodoCompletionRequest(
            new CalendarResourceRevisionReference(Href, "series-1", CalendarEntityKind.Todo, "\"r1\""));

        var result = await CreateService(client).CompleteTodoAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddOccurrenceAsync_StaleRevisionReturnsConflictWithoutBlindRetry()
    {
        var client = ClientReturning(EventFixture("base"));
        var request = Request(new(CalendarTemporalKind.UtcDateTime, "2026-08-20T09:00:00Z")) with
        {
            Snapshot = new CalendarResourceRevisionReference(
                Href,
                "series-1",
                CalendarEntityKind.Event,
                "\"stale\"")
        };

        var result = await CreateService(client).AddOccurrenceAsync(request, CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Conflict);
        await client.Received(1).GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>());
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelOccurrenceAsync_AmbiguousDispatchReconcilesCommittedTruthWithoutSecondPut()
    {
        var original = EventFixture("base");
        var client = ClientReturning(original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            _ => Read("\"r1\"", original),
            _ => Read("\"r2\"", Encoding.UTF8.GetString(dispatched.Span)));

        var result = await CreateService(client).CancelOccurrenceAsync(
            Request(new(CalendarTemporalKind.UtcDateTime, "2026-08-19T09:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreCancellationAsync_RangeCancellationMaterializesOneActiveIndividual()
    {
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T110000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260819T090000Z\r\nDTSTART:20260819T110000Z\r\nSTATUS:CANCELLED\r\nSUMMARY:Cancelled range\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            _ => Read("\"r1\"", original),
            _ => Read("\"r2\"", Encoding.UTF8.GetString(dispatched.Span)));

        var result = await CreateService(client).RestoreOccurrenceCancellationAsync(
            Request(new(CalendarTemporalKind.UtcDateTime, "2026-08-20T09:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("RECURRENCE-ID:20260820T090000Z\r\n");
        outbound.ShouldContain("DTSTART:20260820T110000Z\r\n");
        outbound.ShouldContain("SUMMARY:Cancelled range\r\n");
        outbound.ShouldContain("X-KEEP:opaque\r\n");
        outbound.Split("STATUS:CANCELLED", StringSplitOptions.None).Length.ShouldBe(2);
    }

    private static Task<CalendarEntityPatchResult> ExecuteAsync(CalendarService service, string operation)
    {
        var missing = operation.EndsWith("_missing", StringComparison.Ordinal);
        var identity = new CalendarTemporalValue(
            CalendarTemporalKind.UtcDateTime,
            missing ? "2026-08-20T09:00:00Z" : "2026-08-19T09:00:00Z");
        var request = Request(identity);
        return operation.Replace("_missing", string.Empty, StringComparison.Ordinal) switch
        {
            "add" => service.AddOccurrenceAsync(request, CancellationToken.None),
            "exclude" => service.ExcludeOccurrenceAsync(request, CancellationToken.None),
            "restore_exclusion" => service.RestoreOccurrenceExclusionAsync(request, CancellationToken.None),
            "cancel" => service.CancelOccurrenceAsync(request, CancellationToken.None),
            "restore_cancellation" => service.RestoreOccurrenceCancellationAsync(request, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private static string EventFixture(string state)
    {
        var exclusion = state == "excluded" ? "EXDATE:20260819T090000Z\r\n" : string.Empty;
        var cancelled = state == "cancelled"
            ? "BEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T110000Z\r\nRECURRENCE-ID:20260819T090000Z\r\nDTSTART:20260819T090000Z\r\nSTATUS:CANCELLED\r\nEND:VEVENT\r\n"
            : string.Empty;
        return "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + exclusion
            + "END:VEVENT\r\n"
            + cancelled
            + "END:VCALENDAR\r\n";
    }

    private static string TodoCompletionFixture(string state)
    {
        if (state == "wrong_kind")
        {
            return "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\n"
                + "UID:series-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260818T090000Z\r\n"
                + "RRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        }
        var start = state == "unresolved"
            ? "DTSTART;TZID=Private/Unknown:20260818T090000\r\n"
            : "DTSTART:20260818T090000Z\r\n";
        var rule = state == "unevaluable"
            ? "RRULE:FREQ=DAILY;COUNT=2\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\n"
            : "RRULE:FREQ=DAILY;COUNT=2\r\n";
        var exclusion = state == "excluded" ? "EXDATE:20260819T090000Z\r\n" : string.Empty;
        var overrideContent = state switch
        {
            "cancelled" => "BEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T110000Z\r\nRECURRENCE-ID:20260819T090000Z\r\nDTSTART:20260819T090000Z\r\nSTATUS:CANCELLED\r\nEND:VTODO\r\n",
            "completed" => "BEGIN:VTODO\r\nUID:series-1\r\nDTSTAMP:20260816T110000Z\r\nRECURRENCE-ID:20260819T090000Z\r\nDTSTART:20260819T090000Z\r\nSTATUS:COMPLETED\r\nPERCENT-COMPLETE:100\r\nCOMPLETED:20260816T120000Z\r\nEND:VTODO\r\n",
            _ => string.Empty
        };
        return "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\n"
            + "UID:series-1\r\nDTSTAMP:20260816T100000Z\r\n"
            + start
            + rule
            + exclusion
            + "END:VTODO\r\n"
            + overrideContent
            + "END:VCALENDAR\r\n";
    }

    private static CalendarOccurrenceMutationRequest Request(CalendarTemporalValue identity) => new(
        new CalendarResourceRevisionReference(Href, "series-1", CalendarEntityKind.Event, "\"r1\""),
        identity);

    private static CalendarOccurrenceMutationRequest StatefulRequest(string entityTag, string identity) => new(
        new CalendarResourceRevisionReference(Href, "series-1", CalendarEntityKind.Event, entityTag),
        new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, identity));

    private static CalendarService CreateService(ICalendarClient client, TimeProvider? timeProvider = null) => new(
        client,
        Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example/",
            Username = "user",
            Password = "pass"
        }),
        Substitute.For<ILogger<CalendarService>>(),
        timeProvider ?? new FrozenTimeProvider(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero)),
        Substitute.For<ICalendarEntityIdentityGenerator>());

    private static ICalendarClient ClientReturning(string original, string? observed = null)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = "https://cal.example/events/",
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);
        client.GetCalendarResourceAsync(Href, Arg.Any<CancellationToken>()).Returns(
            Read("\"r1\"", original),
            Read("\"r2\"", observed ?? original));
        return client;
    }

    private static CalendarResourceRead Read(string tag, string content) =>
        CalendarResourceRead.Success(Href, tag, Encoding.UTF8.GetBytes(content));

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset firstUtcNow) : TimeProvider
    {
        public int UtcNowReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow() => firstUtcNow.AddMinutes(UtcNowReadCount++);
    }
}
