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

public sealed class CalendarEntityPatchServiceTests
{
    [Fact]
    public async Task PatchEventAsync_TwoMasterResourceIsOpaqueWithoutAnySemanticWriteRoute()
    {
        const string href = "https://cal.example/events/two-masters.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260821T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("master"),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "No route"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.OpaqueResource);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarScalarPatchOperation.Set, "CONFIRMED")]
    [InlineData(CalendarScalarPatchOperation.Clear, null)]
    public async Task PatchEventAsync_UnknownRegisteredStatusRequiresExactReplacementWithoutWriting(
        CalendarScalarPatchOperation operation,
        string? value)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nSTATUS:FUTURE\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("master"),
            new CalendarEventPatch(Status: new(operation, value))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Broad_patch_digest_excludes_only_generated_entity_last_modified_values()
    {
        const string first = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTIMEZONE\r\nTZID:Custom/Zone\r\nLAST-MODIFIED:20260815T100000Z\r\nEND:VTIMEZONE\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART;TZID=Custom/Zone:20260820T100000\r\nRRULE:FREQ=DAILY;COUNT=2\r\nLAST-MODIFIED:20260817T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var zoneChanged = first.Replace(
            "LAST-MODIFIED:20260815T100000Z",
            "LAST-MODIFIED:20260816T100000Z",
            StringComparison.Ordinal);
        var entityChanged = first.Replace(
            "LAST-MODIFIED:20260817T120000Z",
            "LAST-MODIFIED:20260818T120000Z",
            StringComparison.Ordinal);
        var target = new CalendarMutationTarget("entire-set");

        CalendarEntityCreateFidelity.PatchIntentDigest(Encoding.UTF8.GetBytes(first), target)
            .ShouldNotBe(CalendarEntityCreateFidelity.PatchIntentDigest(Encoding.UTF8.GetBytes(zoneChanged), target));
        CalendarEntityCreateFidelity.PatchIntentDigest(Encoding.UTF8.GetBytes(first), target)
            .ShouldBe(CalendarEntityCreateFidelity.PatchIntentDigest(Encoding.UTF8.GetBytes(entityChanged), target));
    }

    [Fact]
    public async Task PatchEventAsync_ThisAndFutureCreatesRangeAndUpdatesLaterOverridesLosslessly()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=5\r\nSUMMARY:Master\r\nX-MASTER:keep\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260822T100000Z\r\nDTSTART:20260822T120000Z\r\nSUMMARY:Individual\r\nX-INDIVIDUAL:keep\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260823T100000Z\r\nDTSTART:20260823T130000Z\r\nSUMMARY:Later range\r\nX-RANGE:keep\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "this-and-future",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Future"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("RECURRENCE-ID;RANGE=THISANDFUTURE:20260821T100000Z\r\n");
        outbound.ShouldContain("RECURRENCE-ID:20260822T100000Z\r\nDTSTART:20260822T120000Z\r\nSUMMARY:Future\r\nX-INDIVIDUAL:keep\r\n");
        outbound.ShouldContain("RECURRENCE-ID;RANGE=THISANDFUTURE:20260823T100000Z\r\nDTSTART:20260823T130000Z\r\nSUMMARY:Future\r\nX-RANGE:keep\r\n");
        outbound.ShouldContain("SUMMARY:Master\r\nX-MASTER:keep\r\n");
        outbound.Split("RANGE=THISANDFUTURE", StringSplitOptions.None).Length.ShouldBe(3);
    }

    [Fact]
    public async Task PatchEventAsync_ThisAndFutureShiftsLaterExceptionTimingRelatively()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260822T100000Z\r\nDTSTART:20260822T120000Z\r\nDTEND:20260822T130000Z\r\nX-KEEP:offset\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("this-and-future", Utc("2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Start: new(CalendarScalarPatchOperation.Set, Utc("2026-08-21T11:00:00Z")))),
            CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("RECURRENCE-ID;RANGE=THISANDFUTURE:20260821T100000Z\r\n");
        outbound.ShouldContain("DTSTART:20260821T110000Z\r\n");
        outbound.ShouldContain("DTEND:20260821T120000Z\r\n");
        outbound.ShouldContain("RECURRENCE-ID:20260822T100000Z\r\nDTSTART:20260822T130000Z\r\nDTEND:20260822T140000Z\r\nX-KEEP:offset\r\n");
    }

    [Fact]
    public async Task PatchTodoAsync_ThisAndFutureCreatesRangeAndPreservesLaterTodoOverride()
    {
        const string href = "https://cal.example/events/todo-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDUE:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Master\r\nEND:VTODO\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260822T100000Z\r\nDTSTART:20260822T120000Z\r\nDUE:20260822T130000Z\r\nSUMMARY:Exception\r\nX-TODO:keep\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = "https://cal.example/events/",
                DisplayName = "Calendar",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.NotAdvertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchTodoAsync(new CalendarTodoPatchRequest(
            new CalendarResourceRevisionReference(href, "todo-1", CalendarEntityKind.Todo, "\"r1\""),
            new CalendarMutationTarget("this-and-future", Utc("2026-08-21T10:00:00Z")),
            new CalendarTodoPatch(Summary: new(CalendarScalarPatchOperation.Set, "Future todo"))),
            CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("RECURRENCE-ID;RANGE=THISANDFUTURE:20260821T100000Z\r\n");
        outbound.ShouldContain("SUMMARY:Future todo\r\nX-TODO:keep\r\n");
        outbound.Split("SUMMARY:Future todo", StringSplitOptions.None).Length.ShouldBe(3);
    }

    [Theory]
    [InlineData("this-and-future")]
    [InlineData("entire-set")]
    public async Task Recurring_temporal_family_change_requires_exact_replacement(string scope)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var target = scope == "entire-set"
            ? new CalendarMutationTarget(scope)
            : new CalendarMutationTarget(scope, Utc("2026-08-21T10:00:00Z"));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            target,
            new CalendarEventPatch(Start: new(
                CalendarScalarPatchOperation.Set,
                new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-21")))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarEntityKind.Event, "VEVENT")]
    [InlineData(CalendarEntityKind.Todo, "VTODO")]
    public async Task EntireSet_applies_addressed_semantics_to_master_and_every_same_uid_override(
        CalendarEntityKind kind,
        string component)
    {
        const string href = "https://cal.example/events/series.ics";
        var original = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:{component}\r\nUID:series\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master\r\nX-MASTER:keep\r\nEND:{component}\r\nBEGIN:{component}\r\nUID:series\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nSUMMARY:Override\r\nX-OVERRIDE:keep\r\nEND:{component}\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        if (kind == CalendarEntityKind.Todo)
        {
            client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
                new CalendarDescriptor
                {
                    Href = "https://cal.example/events/",
                    DisplayName = "Calendar",
                    DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                    EventSupport = EntityKindSupport.Advertised,
                    TodoSupport = EntityKindSupport.Advertised
                }
            ]);
        }
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));
        var revision = new CalendarResourceRevisionReference(href, "series", kind, "\"r1\"");

        var result = kind == CalendarEntityKind.Event
            ? await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
                revision,
                new CalendarMutationTarget("entire-set"),
                new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "All"))), CancellationToken.None)
            : await CreateService(client).PatchTodoAsync(new CalendarTodoPatchRequest(
                revision,
                new CalendarMutationTarget("entire-set"),
                new CalendarTodoPatch(Summary: new(CalendarScalarPatchOperation.Set, "All"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.Split("SUMMARY:All", StringSplitOptions.None).Length.ShouldBe(3);
        outbound.ShouldContain("X-MASTER:keep\r\n");
        outbound.ShouldContain("X-OVERRIDE:keep\r\n");
    }

    [Theory]
    [InlineData(true, CalendarEntityPatchCode.UpstreamUnavailable)]
    [InlineData(false, CalendarEntityPatchCode.InvalidCalendarData)]
    public async Task EntireSet_recurrence_change_requires_exact_one_to_one_orphan_reconciliation(
        bool complete,
        CalendarEntityPatchCode expectedCode)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=5\r\nEXDATE:20260821T100000Z,20260824T100000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nX-NON-ORPHAN:preserve\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260823T100000Z\r\nDTSTART:20260823T120000Z\r\nX-INDIVIDUAL:keep-unless-authorized\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260822T100000Z\r\nDTSTART:20260822T130000Z\r\nX-RANGE:keep-unless-authorized\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));
        var reconciliations = new List<CalendarOrphanReconciliation>
        {
            new(CalendarOrphanKind.ExceptionDate, Utc("2026-08-24T10:00:00Z")),
            new(CalendarOrphanKind.Override, Utc("2026-08-23T10:00:00Z"), CalendarOrphanOverrideKind.Individual)
        };
        if (complete)
        {
            reconciliations.Add(new(
                CalendarOrphanKind.Override,
                Utc("2026-08-22T10:00:00Z"),
                CalendarOrphanOverrideKind.ThisAndFuture));
        }
        var recurrence = new CalendarRecurrenceSetPatch(
            CalendarScalarPatchOperation.Set,
            new CalendarRecurrenceSetPatchValue(Rule: "FREQ=DAILY;COUNT=2"),
            reconciliations);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("entire-set"),
            new CalendarEventPatch(RecurrenceSet: recurrence, RecurrenceSetAddressed: true)), CancellationToken.None);

        result.Code.ShouldBe(expectedCode, Diagnostic(result));
        if (complete)
        {
            var outbound = Encoding.UTF8.GetString(dispatched.Span);
            outbound.ShouldContain("RRULE:FREQ=DAILY;COUNT=2\r\n");
            outbound.ShouldContain("EXDATE:20260821T100000Z\r\n");
            outbound.ShouldContain("RECURRENCE-ID:20260821T100000Z\r\n");
            outbound.ShouldContain("X-NON-ORPHAN:preserve\r\n");
            outbound.ShouldNotContain("RECURRENCE-ID:20260823T100000Z");
            outbound.ShouldNotContain("RANGE=THISANDFUTURE");
            await client.Received(1).UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await client.DidNotReceive().UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-identity")]
    public async Task EntireSet_recurrence_change_rejects_non_exact_orphan_reconciliation_atomically(
        string mismatch)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=5\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260824T100000Z\r\nDTSTART:20260824T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var valid = new CalendarOrphanReconciliation(
            CalendarOrphanKind.Override,
            Utc("2026-08-24T10:00:00Z"),
            CalendarOrphanOverrideKind.ThisAndFuture);
        IReadOnlyList<CalendarOrphanReconciliation> reconciliations = mismatch switch
        {
            "extra" => [valid, new(CalendarOrphanKind.ExceptionDate, Utc("2026-08-23T10:00:00Z"))],
            "duplicate" => [valid, valid],
            "wrong-kind" => [valid with { OverrideKind = CalendarOrphanOverrideKind.Individual }],
            "wrong-identity" => [valid with { RecurrenceIdentity = Utc("2026-08-23T10:00:00Z") }],
            _ => throw new InvalidOperationException()
        };
        var recurrence = new CalendarRecurrenceSetPatch(
            CalendarScalarPatchOperation.Set,
            new CalendarRecurrenceSetPatchValue(Rule: "FREQ=DAILY;COUNT=2"),
            reconciliations);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("entire-set"),
            new CalendarEventPatch(RecurrenceSet: recurrence, RecurrenceSetAddressed: true)), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidCalendarData, Diagnostic(result));
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("FREQ=DAILY;COUNT=2\r\nATTENDEE:mailto:injected@example.test", false)]
    [InlineData("NOT-A-RULE", false)]
    [InlineData("FREQ=DAILY;COUNT=2", true)]
    public async Task EntireSet_recurrence_change_validates_safe_rule_and_temporal_family_before_write(
        string rule,
        bool crossFamilyDate)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var recurrence = new CalendarRecurrenceSetPatch(
            CalendarScalarPatchOperation.Set,
            new CalendarRecurrenceSetPatchValue(
                Rule: rule,
                RecurrenceDates: crossFamilyDate
                    ? [new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-21")]
                    : null),
            []);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("entire-set"),
            new CalendarEventPatch(RecurrenceSet: recurrence, RecurrenceSetAddressed: true)), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidCalendarData, Diagnostic(result));
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EntireSet_updates_last_modified_on_every_changed_non_derived_component()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master\r\nLAST-MODIFIED:20260815T100000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nSUMMARY:Override\r\nLAST-MODIFIED:20260815T100000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260821T100000Z\r\nDTSTART:20260821T130000Z\r\nSUMMARY:Range\r\nLAST-MODIFIED;DERIVED=TRUE:20260815T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("entire-set"),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "All"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.Split("LAST-MODIFIED:20260817T120000Z", StringSplitOptions.None).Length.ShouldBe(3);
        outbound.ShouldContain("LAST-MODIFIED;DERIVED=TRUE:20260815T100000Z");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntireSet_recurrence_change_rejects_unevaluable_existing_structure(bool backwardRange)
    {
        const string href = "https://cal.example/events/event-1.ics";
        var recurrence = backwardRange
            ? "RRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID;RANGE=THISANDPRIOR:20260821T100000Z\r\nDTSTART:20260821T110000Z\r\n"
            : "RRULE:FREQ=DAILY;COUNT=3\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\n";
        var original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\n"
            + recurrence + "END:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var recurrencePatch = new CalendarRecurrenceSetPatch(
            CalendarScalarPatchOperation.Set,
            new CalendarRecurrenceSetPatchValue(Rule: "FREQ=DAILY;COUNT=2"),
            []);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("entire-set"),
            new CalendarEventPatch(RecurrenceSet: recurrencePatch, RecurrenceSetAddressed: true)),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.RecurrenceUnevaluable, Diagnostic(result));
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EntireSet_recurrence_change_rejects_this_and_prior_input_as_unevaluable()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var recurrencePatch = new CalendarRecurrenceSetPatch(
            CalendarScalarPatchOperation.Set,
            new CalendarRecurrenceSetPatchValue(
                Rule: "FREQ=DAILY;COUNT=3",
                Overrides:
                [
                    new CalendarRecurrenceOverridePatchValue(
                        Utc("2026-08-21T10:00:00Z"),
                        CalendarEntityKind.Event,
                        CalendarRecurrenceOverrideStatus.Active,
                        CalendarRecurrenceOverrideRange.ThisAndPrior)
                ]),
            []);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("entire-set"),
            new CalendarEventPatch(RecurrenceSet: recurrencePatch, RecurrenceSetAddressed: true)),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.RecurrenceUnevaluable, Diagnostic(result));
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EntireSet_equivalent_recurrence_reset_is_no_change_and_accepts_empty_overrides()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:COUNT=3;FREQ=DAILY\r\nEXDATE:20260821T100000Z,20260822T100000Z\r\nSUMMARY:keep-order\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var recurrencePatch = new CalendarRecurrenceSetPatch(
            CalendarScalarPatchOperation.Set,
            new CalendarRecurrenceSetPatchValue(
                Rule: "FREQ=DAILY;COUNT=3",
                ExceptionDates: [Utc("2026-08-21T10:00:00Z"), Utc("2026-08-22T10:00:00Z")],
                Overrides: []),
            []);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("entire-set"),
            new CalendarEventPatch(RecurrenceSet: recurrencePatch, RecurrenceSetAddressed: true)),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.NoChange, Diagnostic(result));
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, CalendarEntityPatchCode.NoChange)]
    [InlineData(false, CalendarEntityPatchCode.InvalidCalendarData)]
    public async Task EntireSet_recurrence_override_input_is_an_exact_lossless_assertion(
        bool exact,
        CalendarEntityPatchCode expected)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nDTEND:20260821T130000Z\r\nSUMMARY:preserve-losslessly\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var requestedOverride = new CalendarRecurrenceOverridePatchValue(
            Utc("2026-08-21T10:00:00Z"),
            CalendarEntityKind.Event,
            CalendarRecurrenceOverrideStatus.Active,
            CalendarRecurrenceOverrideRange.ThisAndFuture,
            Utc(exact ? "2026-08-21T12:00:00Z" : "2026-08-21T11:00:00Z"),
            Utc("2026-08-21T13:00:00Z"));
        var recurrencePatch = new CalendarRecurrenceSetPatch(
            CalendarScalarPatchOperation.Set,
            new CalendarRecurrenceSetPatchValue(
                Rule: "FREQ=DAILY;COUNT=3",
                Overrides: [requestedOverride]),
            []);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("entire-set"),
            new CalendarEventPatch(RecurrenceSet: recurrencePatch, RecurrenceSetAddressed: true)),
            CancellationToken.None);

        result.Code.ShouldBe(expected, Diagnostic(result));
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EntireSet_explicit_override_assertion_does_not_wildcard_omitted_temporal_fields()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var recurrencePatch = new CalendarRecurrenceSetPatch(
            CalendarScalarPatchOperation.Set,
            new CalendarRecurrenceSetPatchValue(
                Rule: "FREQ=DAILY;COUNT=2",
                Overrides:
                [
                    new CalendarRecurrenceOverridePatchValue(
                        Utc("2026-08-21T10:00:00Z"),
                        CalendarEntityKind.Event,
                        CalendarRecurrenceOverrideStatus.Active)
                ]),
            []);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("entire-set"),
            new CalendarEventPatch(RecurrenceSet: recurrencePatch, RecurrenceSetAddressed: true)),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidCalendarData, Diagnostic(result));
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EntireSet_override_assertion_distinguishes_individual_and_range_at_same_identity()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260821T100000Z\r\nDTSTART:20260821T130000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        var identity = Utc("2026-08-21T10:00:00Z");
        var recurrencePatch = new CalendarRecurrenceSetPatch(
            CalendarScalarPatchOperation.Set,
            new CalendarRecurrenceSetPatchValue(
                Rule: "FREQ=DAILY;COUNT=2",
                Overrides:
                [
                    new CalendarRecurrenceOverridePatchValue(
                        identity,
                        CalendarEntityKind.Event,
                        CalendarRecurrenceOverrideStatus.Active,
                        MovedStart: Utc("2026-08-21T12:00:00Z")),
                    new CalendarRecurrenceOverridePatchValue(
                        identity,
                        CalendarEntityKind.Event,
                        CalendarRecurrenceOverrideStatus.Active,
                        CalendarRecurrenceOverrideRange.ThisAndFuture,
                        Utc("2026-08-21T13:00:00Z"))
                ]),
            []);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("entire-set"),
            new CalendarEventPatch(RecurrenceSet: recurrencePatch, RecurrenceSetAddressed: true)),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.NoChange, Diagnostic(result));
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ThisAndFuture_shifts_sparse_later_override_from_its_effective_identity()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260822T100000Z\r\nSTATUS:CANCELLED\r\nX-SPARSE:keep\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget("this-and-future", Utc("2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Start: new(CalendarScalarPatchOperation.Set, Utc("2026-08-21T11:00:00Z")))),
            CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("RECURRENCE-ID:20260822T100000Z\r\nSTATUS:CANCELLED\r\nX-SPARSE:keep\r\nDTSTART:20260822T110000Z\r\n");
        outbound.ShouldNotContain("RECURRENCE-ID:20260822T100000Z\r\nSTATUS:CANCELLED\r\nX-SPARSE:keep\r\nDTSTART:20260821T110000Z\r\n");
    }

    [Fact]
    public async Task ThisAndFuture_shifts_sparse_later_todo_start_and_due_relatively()
    {
        const string href = "https://cal.example/tasks/todo-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDUE:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nEND:VTODO\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260822T100000Z\r\nSTATUS:CANCELLED\r\nX-SPARSE:keep\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = "https://cal.example/tasks/",
                DisplayName = "Tasks",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.NotAdvertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchTodoAsync(new CalendarTodoPatchRequest(
            new CalendarResourceRevisionReference(href, "todo-1", CalendarEntityKind.Todo, "\"r1\""),
            new CalendarMutationTarget("this-and-future", Utc("2026-08-21T10:00:00Z")),
            new CalendarTodoPatch(Start: new(CalendarScalarPatchOperation.Set, Utc("2026-08-21T11:00:00Z")))),
            CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("RECURRENCE-ID:20260822T100000Z");
        outbound.ShouldContain("DTSTART:20260822T110000Z");
        outbound.ShouldContain("DUE:20260822T120000Z");
        outbound.ShouldContain("X-SPARSE:keep");
    }

    [Fact]
    public async Task PatchEventAsync_ChangesOnlySummaryAndLastModifiedWithExactReviewedRevision()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nCREATED:20260815T100000Z\r\nLAST-MODIFIED:20260815T100000Z\r\nSEQUENCE:7\r\nSUMMARY:Original\r\nCOLOR:#112233\r\nIMAGE:https://e/x.png\r\nCONFERENCE:https://e/c\r\nLOCATION-TYPE:office\r\nX-KEEP:1\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        const string expected = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nCREATED:20260815T100000Z\r\nLAST-MODIFIED:20260817T120000Z\r\nSEQUENCE:7\r\nSUMMARY:Updated\r\nCOLOR:#112233\r\nIMAGE:https://e/x.png\r\nCONFERENCE:https://e/c\r\nLOCATION-TYPE:office\r\nX-KEEP:1\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
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
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
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
    public async Task PatchEventAsync_CannotConvertAnExistingTodoAndWritesNothing()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\n"
            + "BEGIN:VTODO\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nSUMMARY:Todo\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, content);

        var result = await CreateService(client).PatchEventAsync(EventRequest(
            href,
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Event"))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.EntityKindMismatch);
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
    public async Task PatchEventAsync_OneOccurrenceCreatesCompleteMovedOverrideWithOriginalIdentity()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Master\r\nDESCRIPTION:Inherited\r\nX-KEEP;P=One,one:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));
        var target = new CalendarMutationTarget(
            "one-occurrence",
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z"));
        var patch = new CalendarEventPatch(
            Summary: new(CalendarScalarPatchOperation.Set, "Moved occurrence"),
            Start: new(
                CalendarScalarPatchOperation.Set,
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T15:00:00Z")));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            target,
            patch), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("DTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Master\r\n");
        outbound.ShouldContain("RECURRENCE-ID:20260821T100000Z\r\n");
        outbound.ShouldContain("DTSTART:20260821T150000Z\r\nDTEND:20260821T160000Z\r\n");
        outbound.ShouldContain("SUMMARY:Moved occurrence\r\nDESCRIPTION:Inherited\r\nX-KEEP;P=One,one:opaque\r\n");
        outbound.Split("BEGIN:VEVENT", StringSplitOptions.None).Length.ShouldBe(3);
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceInheritsNearestRangeOverrideWithoutRangeMarker()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID;X-KEEP=one,two;RANGE=THISANDFUTURE:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nDTEND:20260821T140000Z\r\nSUMMARY:Range effective\r\nX-RANGE-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-23T10:00:00Z")),
            new CalendarEventPatch(Description: new(CalendarScalarPatchOperation.Set, "Patched"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        var individual = outbound[outbound.LastIndexOf("BEGIN:VEVENT", StringComparison.Ordinal)..];
        individual.ShouldContain("RECURRENCE-ID;X-KEEP=one,two:20260823T100000Z\r\n");
        individual.ShouldContain("DTSTART:20260823T120000Z\r\n");
        individual.ShouldContain("DTEND:20260823T140000Z\r\n");
        individual.ShouldContain("SUMMARY:Range effective\r\n");
        individual.ShouldContain("X-RANGE-KEEP:opaque\r\n");
        individual.ShouldContain("DESCRIPTION:Patched\r\n");
        individual.ShouldNotContain("RANGE=THISANDFUTURE");
        outbound.Split("RANGE=THISANDFUTURE", StringSplitOptions.None).Length.ShouldBe(2);
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrencePreservesCancelledRangeState()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nSTATUS:CANCELLED\r\nSUMMARY:Cancelled range\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-22T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Still cancelled"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var individual = Encoding.UTF8.GetString(dispatched.Span);
        individual = individual[individual.LastIndexOf("BEGIN:VEVENT", StringComparison.Ordinal)..];
        individual.ShouldContain("RECURRENCE-ID:20260822T100000Z\r\n");
        individual.ShouldContain("DTSTART:20260822T120000Z\r\n");
        individual.ShouldContain("STATUS:CANCELLED\r\n");
        individual.ShouldContain("SUMMARY:Still cancelled\r\n");
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceRejectsNonexistentIdentityWithoutWriting()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-24T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must not be added"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.NotFound);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceRejectsStaleSeriesRevisionBeforeTargetEvaluation()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string malformed = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, malformed, "\"r2\"");

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must not be evaluated"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.Conflict);
        result.Phase.ShouldBe(CalendarEntityPatchPhase.TargetRevision);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceRejectsMalformedIdentityAsCallerInputWithoutWriting()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "not-a-date-time")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must not be evaluated"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceRejectsBareNonRecurringMasterStartWithoutWriting()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nSUMMARY:Not recurring\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-20T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must remain master"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.NotFound);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchTodoAsync_OneOccurrenceRejectsBareNonRecurringMasterStartWithoutWriting()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string href = "https://cal.example/todos/todo-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nSUMMARY:Not recurring\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Todos",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.NotAdvertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(Read(href, "\"r1\"", original));

        var result = await CreateService(client).PatchTodoAsync(new CalendarTodoPatchRequest(
            new CalendarResourceRevisionReference(href, "todo-1", CalendarEntityKind.Todo, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-20T10:00:00Z")),
            new CalendarTodoPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must remain master"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.NotFound);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarScalarPatchOperation.Set, "CANCELLED")]
    [InlineData(CalendarScalarPatchOperation.Set, "CONFIRMED")]
    [InlineData(CalendarScalarPatchOperation.Clear, null)]
    public async Task PatchEventAsync_OneOccurrenceRejectsCancellationAndRestorationThroughGenericStatus(
        CalendarScalarPatchOperation operation,
        string? value)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T100000Z\r\nSTATUS:CANCELLED\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Status: new(operation, value))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarScalarPatchOperation.Set, "CONFIRMED")]
    [InlineData(CalendarScalarPatchOperation.Clear, null)]
    public async Task PatchEventAsync_OneOccurrenceAllowsOrdinaryStatusMutation(
        CalendarScalarPatchOperation operation,
        string? value)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T100000Z\r\nSTATUS:TENTATIVE\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Status: new(operation, value))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceRejectsRdatePeriodBeforeWriting()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDURATION:PT1H\r\nRDATE;VALUE=PERIOD:20260821T100000Z/PT3H\r\nSUMMARY:Master\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must not lose the period span"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.UnsupportedCapability);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceRejectsAnyRdatePeriodBeforeWriting()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nRDATE;VALUE=PERIOD:20260824T100000Z/PT3H\r\nSUMMARY:Master\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must not rewrite PERIOD"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.UnsupportedCapability);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("RDATE")]
    [InlineData("rdate")]
    public async Task PatchEventAsync_OneOccurrenceTargetsExistingTemporalRdateWithoutImplicitAdd(
        string propertyName)
    {
        const string href = "https://cal.example/events/event-1.ics";
        var original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\n"
            + propertyName + ":20260824T100000Z\r\nSUMMARY:Master\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-24T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "RDATE occurrence"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var individual = Encoding.UTF8.GetString(dispatched.Span);
        individual = individual[individual.LastIndexOf("BEGIN:VEVENT", StringComparison.Ordinal)..];
        individual.ShouldContain("RECURRENCE-ID:20260824T100000Z\r\n");
        individual.ShouldContain("DTSTART:20260824T100000Z\r\n");
        individual.ShouldContain("DTEND:20260824T110000Z\r\n");
        individual.ShouldContain("SUMMARY:RDATE occurrence\r\n");
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceTreatsLowerCaseRecurrencePropertyAsSeriesMembership()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\nrdate:20260824T100000Z\r\nSUMMARY:Master\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-20T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "First occurrence"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("RRULE:FREQ=DAILY;COUNT=3\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\n")]
    [InlineData("RRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID;RANGE=THISANDPRIOR:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\n")]
    public async Task PatchEventAsync_OneOccurrenceReturnsRecurrenceUnevaluableForUnsupportedStructure(
        string recurrence)
    {
        const string href = "https://cal.example/events/event-1.ics";
        var original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\n"
            + recurrence + "SUMMARY:Master\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must not evaluate"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.RecurrenceUnevaluable);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Phase.ShouldBe(CalendarEntityPatchPhase.CompleteResourceSemantics);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceRejectsMalformedIdentityBeforeUnsupportedRecurrenceStructure()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\nSUMMARY:Master\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "not-a-date-time")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must not evaluate"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(1999, false)]
    [InlineData(2000, true)]
    public async Task PatchEventAsync_OneOccurrenceEnforcesExactPerEntityOccurrenceLimit(
        int daysAfterStart,
        bool exhausted)
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260101T100000Z\r\nRRULE:FREQ=DAILY\r\nSUMMARY:Master\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));
        var targetDate = new DateOnly(2026, 1, 1).AddDays(daysAfterStart)
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, targetDate + "T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Beyond cap"))), CancellationToken.None);

        if (exhausted)
        {
            result.Code.ShouldBe(CalendarEntityPatchCode.LimitExhausted);
            result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
            result.Phase.ShouldBe(CalendarEntityPatchPhase.CompleteResourceSemantics);
            await client.DidNotReceive().UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        }
        else
        {
            result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
            await client.Received(1).UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrencePropagatesCancellationDuringBoundedMembershipEvaluation()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260101T100000Z\r\nRRULE:FREQ=DAILY\r\nSUMMARY:Master\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2030-01-01T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must not write"))), cancellation.Token);

        await Should.ThrowAsync<OperationCanceledException>(action);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceDoesNotDeleteSuppressedOverrideWithoutRestorationIntent()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEXDATE:20260821T100000Z\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nSUMMARY:Suppressed override\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must remain suppressed"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidInput);
        result.Snapshot!.AuthoritativeUtf8.Span.SequenceEqual(Encoding.UTF8.GetBytes(original)).ShouldBeTrue();
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrencePrefersExistingIndividualOverRangeAndPreservesCancellationState()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260821T100000Z\r\nDTSTART:20260821T140000Z\r\nSUMMARY:Range\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nSTATUS:CANCELLED\r\nSUMMARY:Cancelled individual\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Still cancelled"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        outbound.ShouldContain("RECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nSTATUS:CANCELLED\r\nSUMMARY:Still cancelled\r\nX-KEEP:opaque\r\n");
        outbound.ShouldContain("RECURRENCE-ID;RANGE=THISANDFUTURE:20260821T100000Z\r\nDTSTART:20260821T140000Z\r\nSUMMARY:Range\r\n");
        outbound.Split("BEGIN:VEVENT", StringSplitOptions.None).Length.ShouldBe(4);
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceCompletesCancelledOverrideWithoutStartFromEffectiveMaster()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Master\r\nDESCRIPTION:Inherited\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T120000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nSTATUS:CANCELLED\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Still cancelled"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var individual = Encoding.UTF8.GetString(dispatched.Span);
        individual = individual[individual.LastIndexOf("BEGIN:VEVENT", StringComparison.Ordinal)..];
        individual.ShouldContain("RECURRENCE-ID:20260821T100000Z\r\n");
        individual.ShouldContain("DTSTART:20260821T100000Z\r\n");
        individual.ShouldContain("DTEND:20260821T110000Z\r\n");
        individual.ShouldContain("SUMMARY:Still cancelled\r\n");
        individual.ShouldContain("DESCRIPTION:Inherited\r\n");
        individual.ShouldContain("STATUS:CANCELLED\r\n");
        individual.ShouldContain("X-KEEP:opaque\r\n");
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceRejectsNonCancelledOverrideWithoutStartInsteadOfFillingIt()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T120000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nSUMMARY:Skeletal but active\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Must not be filled"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.InvalidCalendarData);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrencePreservesIntentionalAbsenceOnCompleteExistingEventOverride()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDTEND:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Master\r\nDESCRIPTION:Inherited description\r\nATTENDEE:mailto:first@example.test\r\nATTENDEE:mailto:second@example.test\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:Reminder\r\nTRIGGER:-PT15M\r\nEND:VALARM\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T120000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nSUMMARY:Explicit individual\r\nX-KEEP:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Location: new(CalendarScalarPatchOperation.Set, "Patched"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var individual = Encoding.UTF8.GetString(dispatched.Span);
        individual = individual[individual.LastIndexOf("BEGIN:VEVENT", StringComparison.Ordinal)..];
        individual.ShouldContain("DTSTART:20260821T120000Z\r\n");
        individual.ShouldContain("SUMMARY:Explicit individual\r\n");
        individual.ShouldContain("X-KEEP:opaque\r\n");
        individual.ShouldContain("LOCATION:Patched\r\n");
        individual.ShouldNotContain("DTEND");
        individual.ShouldNotContain("DESCRIPTION:Inherited description");
        individual.ShouldNotContain("ATTENDEE");
        individual.ShouldNotContain("VALARM");
        individual.ShouldNotContain("RRULE");
    }

    [Fact]
    public async Task PatchTodoAsync_OneOccurrenceCreatesCompleteSameUidTodoOverride()
    {
        const string calendarHref = "https://cal.example/events/";
        const string href = "https://cal.example/events/todo-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDUE:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master todo\r\nDESCRIPTION:Inherited\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Todos",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.NotAdvertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(Read(href, "\"r1\"", original));
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchTodoAsync(new CalendarTodoPatchRequest(
            new CalendarResourceRevisionReference(href, "todo-1", CalendarEntityKind.Todo, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarTodoPatch(Summary: new(CalendarScalarPatchOperation.Set, "One todo"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        var individual = outbound[outbound.LastIndexOf("BEGIN:VTODO", StringComparison.Ordinal)..];
        individual.ShouldContain("UID:todo-1\r\n");
        individual.ShouldContain("RECURRENCE-ID:20260821T100000Z\r\n");
        individual.ShouldContain("DTSTART:20260821T100000Z\r\n");
        individual.ShouldContain("DUE:20260821T110000Z\r\n");
        individual.ShouldContain("SUMMARY:One todo\r\n");
        individual.ShouldContain("DESCRIPTION:Inherited\r\n");
        individual.ShouldNotContain("RRULE");
    }

    [Fact]
    public async Task PatchTodoAsync_OneOccurrencePreservesIntentionalAbsenceOnCompleteExistingTodoOverride()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string href = "https://cal.example/todos/todo-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nDUE:20260820T110000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master todo\r\nDESCRIPTION:Inherited todo\r\nCATEGORIES:Work\r\nCATEGORIES:Home\r\nEND:VTODO\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260816T120000Z\r\nRECURRENCE-ID:20260821T100000Z\r\nDTSTART:20260821T120000Z\r\nSUMMARY:Explicit todo\r\nX-KEEP:opaque\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Todos",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.NotAdvertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(Read(href, "\"r1\"", original));
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchTodoAsync(new CalendarTodoPatchRequest(
            new CalendarResourceRevisionReference(href, "todo-1", CalendarEntityKind.Todo, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarTodoPatch(Description: new(CalendarScalarPatchOperation.Set, "Patched todo"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var individual = Encoding.UTF8.GetString(dispatched.Span);
        individual = individual[individual.LastIndexOf("BEGIN:VTODO", StringComparison.Ordinal)..];
        individual.ShouldContain("DTSTART:20260821T120000Z\r\n");
        individual.ShouldContain("SUMMARY:Explicit todo\r\n");
        individual.ShouldContain("DESCRIPTION:Patched todo\r\n");
        individual.ShouldContain("X-KEEP:opaque\r\n");
        individual.ShouldNotContain("DUE");
        individual.ShouldNotContain("CATEGORIES");
        individual.ShouldNotContain("RRULE");
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceKeepsNamedZoneWallTimeAcrossDst()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260301T100000Z\r\nDTSTART;TZID=America/New_York:20260307T090000\r\nDTEND;TZID=America/New_York:20260307T100000\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:DST series\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);
        ReadOnlyMemory<byte> dispatched = default;
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => dispatched = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(
                    CalendarTemporalKind.ZonedDateTime,
                    "2026-03-09T09:00:00",
                    "America/New_York")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "After DST"))), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted, Diagnostic(result));
        var outbound = Encoding.UTF8.GetString(dispatched.Span);
        var individual = outbound[outbound.LastIndexOf("BEGIN:VEVENT", StringComparison.Ordinal)..];
        individual.ShouldContain("RECURRENCE-ID;TZID=America/New_York:20260309T090000\r\n");
        individual.ShouldContain("DTSTART;TZID=America/New_York:20260309T090000\r\n");
        individual.ShouldContain("DTEND;TZID=America/New_York:20260309T100000\r\n");
    }

    [Fact]
    public async Task PatchEventAsync_OneOccurrenceInheritedNoChangeDoesNotMaterializeOverride()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Inherited\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = ClientReturning(href, original);

        var result = await CreateService(client).PatchEventAsync(new CalendarEventPatchRequest(
            new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
            new CalendarMutationTarget(
                "one-occurrence",
                new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z")),
            new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Inherited"))), CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityPatchCode.NoChange);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
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

    private static ICalendarClient ClientReturning(string href, string content, string entityTag = "\"r1\"")
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
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(Read(href, entityTag, content));
        return client;
    }

    private static CalendarEventPatchRequest EventRequest(string href, CalendarEventPatch patch) => new(
        new CalendarResourceRevisionReference(href, "event-1", CalendarEntityKind.Event, "\"r1\""),
        new CalendarMutationTarget("master"),
        patch);

    private static CalendarResourceRead Read(string href, string tag, string content) =>
        CalendarResourceRead.Success(href, tag, Encoding.UTF8.GetBytes(content));

    private static CalendarTemporalValue Utc(string value) =>
        new(CalendarTemporalKind.UtcDateTime, value);

    private static string Diagnostic(CalendarEntityPatchResult result) => result.Code + ":" + string.Join(
        ',',
        result.Snapshot?.Diagnostics.Select(item => item.Code) ?? []);

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
