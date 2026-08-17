using System.Collections.Concurrent;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

[Collection("RadicaleConformanceCollection")]
public sealed class RadicaleConformanceHarnessTests(RadicaleConformanceFixture fixture, ITestOutputHelper output)
{
    internal const string ConformanceUsername = RadicaleConformanceFixture.Username;
    internal const string ConformancePassword = RadicaleConformanceFixture.Password;

    [Fact]
    public void Pinned_profile_records_the_runtime_and_selected_variant()
    {
        output.WriteLine(JsonSerializer.Serialize(fixture.Runtime));
        fixture.Runtime.IndexDigest.ShouldBe(RadicaleConformanceFixture.IndexDigest);
        new[] { RadicaleConformanceFixture.Amd64ManifestDigest, RadicaleConformanceFixture.Arm64ManifestDigest }
            .ShouldContain(fixture.Runtime.ResolvedPlatformManifestDigest);
        fixture.Runtime.ResolvedPlatformManifestDigest.ShouldBe(fixture.Runtime.RuntimeArchitecture switch
        {
            "x86_64" => RadicaleConformanceFixture.Amd64ManifestDigest,
            "aarch64" => RadicaleConformanceFixture.Arm64ManifestDigest,
            _ => throw new InvalidOperationException($"Unsupported architecture {fixture.Runtime.RuntimeArchitecture}")
        });
        fixture.Runtime.RadicaleVersion.ShouldBe("3.7.8");
        fixture.Runtime.PythonVersion.ShouldBe("3.14.7");
        fixture.Runtime.VobjectVersion.ShouldBe("0.9.9");
        fixture.Runtime.RuntimeTimeZone.ShouldBe(fixture.Variant.TimeZone);
        fixture.Runtime.StrictPreconditions.ShouldBe(fixture.Variant.StrictPreconditions);
    }

    [Fact]
    public async Task Pinned_profile_preserves_occurrence_boundary_dst_leap_range_and_typed_failures()
    {
        var calendarHref = $"{fixture.BaseUrl}/conformance/conformance/";
        var boundaryFrom = await PutAndGetAsync(calendarHref, "boundary-from.ics", Event(
            "boundary-from", "DTSTART:20260816T100000Z\r\nDURATION:PT1M\r\n"));
        _ = await PutAndGetAsync(calendarHref, "boundary-to.ics", Event(
            "boundary-to", "DTSTART:20260816T110000Z\r\nDURATION:PT1M\r\n"));
        _ = await PutAndGetAsync(calendarHref, "dst.ics", Event(
            "dst", "DTSTART;TZID=America/New_York:20260307T100000\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=3\r\n"));
        _ = await PutAndGetAsync(calendarHref, "leap.ics", Event(
            "leap", "DTSTART:20240229T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=YEARLY;COUNT=3\r\n"));
        var range = await PutAndGetAsync(calendarHref, "range.ics", RangeEvent());
        await using var provider = CreateProvider(fixture.BaseUrl, calendarHref);
        var service = provider.GetRequiredService<ICalendarService>();
        var scope = CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref));

        var boundary = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-16T11:00:00Z")), TestContext.Current.CancellationToken);
        var dst = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2026-03-08T14:30:00Z"),
            DateTimeOffset.Parse("2026-03-08T14:45:00Z")), TestContext.Current.CancellationToken);
        var leap = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2028-02-29T10:30:00Z"),
            DateTimeOffset.Parse("2028-02-29T10:45:00Z")), TestContext.Current.CancellationToken);
        var moved = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2026-08-17T13:30:00Z"),
            DateTimeOffset.Parse("2026-08-17T13:45:00Z")), TestContext.Current.CancellationToken);

        boundary.Items.ShouldHaveSingleItem().Snapshot.Projection.EntityUid.ShouldBe("boundary-from");
        boundary.Items[0].Snapshot.AuthoritativeUtf8.ToArray().ShouldBe(boundaryFrom.Utf8);
        dst.Items.ShouldHaveSingleItem().Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-03-08T14:00:00Z");
        leap.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2028-02-29T10:00:00Z");
        var movedOccurrence = moved.Items.ShouldHaveSingleItem();
        movedOccurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-17T09:00:00Z");
        movedOccurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-17T13:00:00Z");
        movedOccurrence.Snapshot.AuthoritativeUtf8.ToArray().ShouldBe(range.Utf8);

        var unresolved = await PutAndGetAsync(calendarHref, "unresolved.ics", Event(
            "unresolved", "DTSTART;TZID=Private/Unknown:20260816T100000\r\nDURATION:PT1H\r\n"));
        var unresolvedResult = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2026-08-16T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-17T00:00:00Z")), TestContext.Current.CancellationToken);
        unresolvedResult.Code.ShouldBe(CalendarOccurrenceQueryCode.TemporalUnresolved);
        unresolvedResult.Items.ShouldBeEmpty();
        await DeleteAsync(unresolved, TestContext.Current.CancellationToken);

        var unevaluable = await PutAndGetAsync(calendarHref, "unevaluable.ics", Event(
            "unevaluable", "DTSTART:20260816T100000Z\r\nDURATION:PT1H\r\n"
            + "RRULE:FREQ=DAILY;COUNT=2\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\n"));
        var unevaluableResult = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2026-08-16T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-17T00:00:00Z")), TestContext.Current.CancellationToken);
        unevaluableResult.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        unevaluableResult.Items.ShouldBeEmpty();
        await DeleteAsync(unevaluable, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pinned_profile_creates_nonrecurring_event_and_todo_with_authoritative_strong_snapshots()
    {
        var calendarHref = $"{fixture.BaseUrl}/conformance/conformance/";
        var requestTrace = new ConcurrentQueue<string>();
        await using var provider = CreateProvider(fixture.BaseUrl, calendarHref, requestTrace);
        var service = provider.GetRequiredService<ICalendarService>();
        var destination = CalendarCreateDestination.Selected(new CalendarReference(Href: calendarHref));
        var eventStructuredData = new CalendarStructuredData(
            Organizer: new CalendarNamedUri(
                "mailto:owner@example.test",
                "Owner",
                [new CalendarParameter("X-STORED", ["event-marker"])]),
            Attachments:
            [
                new CalendarNamedUri(
                    "https://storage.example.test/event-document",
                    "Agenda",
                    [new CalendarParameter("FMTTYPE", ["text/plain"])])
            ]);
        var todoStructuredData = new CalendarStructuredData(
            Links:
            [
                new CalendarNamedUri(
                    "https://storage.example.test/todo-reference",
                    "Reference",
                    [new CalendarParameter("X-STORED", ["todo-marker"])])
            ],
            Comments: [new CalendarTextValue("Keep this exact comment", [])]);

        var createdEvent = await service.CreateEventAsync(
            new CalendarEventCreateRequest(
                destination,
                "pinned-create-event",
                new CalendarEventCreateFields(
                    Summary: "Pinned create event",
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-18T13:00:00Z"),
                    End: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-18T14:00:00Z"),
                    StructuredData: eventStructuredData)),
            TestContext.Current.CancellationToken);
        var createdTodo = await service.CreateTodoAsync(
            new CalendarTodoCreateRequest(
                destination,
                "pinned-create-todo",
                new CalendarTodoCreateFields(
                    Summary: "Pinned create todo",
                    Due: new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-19"),
                    StructuredData: todoStructuredData)),
            TestContext.Current.CancellationToken);

        createdEvent.Code.ShouldBe(
            CalendarEntityCreateCode.Success,
            DescribeCreateResult(createdEvent, requestTrace));
        createdTodo.Code.ShouldBe(
            CalendarEntityCreateCode.Success,
            DescribeCreateResult(createdTodo, requestTrace));
        createdEvent.MutationState.ShouldBe(CalendarMutationState.Committed);
        createdTodo.MutationState.ShouldBe(CalendarMutationState.Committed);
        var eventHref = AssertAuthoritativeCreate(
            createdEvent.Snapshot!,
            calendarHref,
            "pinned-create-event",
            "VEVENT");
        var todoHref = AssertAuthoritativeCreate(
            createdTodo.Snapshot!,
            calendarHref,
            "pinned-create-todo",
            "VTODO");
        AssertLosslessProperty(
            createdEvent.Snapshot!,
            "VEVENT",
            "ORGANIZER",
            CalendarPropertyValueType.Uri,
            "mailto:owner@example.test",
            new CalendarParameter("CN", ["Owner"]),
            new CalendarParameter("X-STORED", ["event-marker"]));
        AssertLosslessProperty(
            createdEvent.Snapshot!,
            "VEVENT",
            "ATTACH",
            CalendarPropertyValueType.Uri,
            "https://storage.example.test/event-document",
            new CalendarParameter("LABEL", ["Agenda"]),
            new CalendarParameter("FMTTYPE", ["text/plain"]));
        AssertLosslessProperty(
            createdTodo.Snapshot!,
            "VTODO",
            "LINK",
            CalendarPropertyValueType.Uri,
            "https://storage.example.test/todo-reference",
            new CalendarParameter("LABEL", ["Reference"]),
            new CalendarParameter("VALUE", ["URI"]),
            new CalendarParameter("X-STORED", ["todo-marker"]));
        AssertLosslessProperty(
            createdTodo.Snapshot!,
            "VTODO",
            "COMMENT",
            CalendarPropertyValueType.Text,
            "Keep this exact comment");

        await fixture.DeleteResourceHrefAsync(
            eventHref,
            createdEvent.Snapshot!.EntityTag,
            TestContext.Current.CancellationToken);
        await fixture.DeleteResourceHrefAsync(
            todoHref,
            createdTodo.Snapshot!.EntityTag,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pinned_profile_creates_recurring_event_and_todo_with_dst_exclusions_and_complete_overrides()
    {
        var calendarHref = $"{fixture.BaseUrl}/conformance/conformance/";
        var requestTrace = new ConcurrentQueue<string>();
        await using var provider = CreateProvider(fixture.BaseUrl, calendarHref, requestTrace);
        var service = provider.GetRequiredService<ICalendarService>();
        var destination = CalendarCreateDestination.Selected(new CalendarReference(Href: calendarHref));
        var eventStart = Zoned("2026-03-07T09:00:00", "America/New_York");
        var eventOverrideIdentity = Zoned("2026-03-08T09:00:00", "America/New_York");
        var eventRequest = new CalendarEventCreateRequest(
            destination,
            "pinned-recurring-event",
            new CalendarEventCreateFields(
                Summary: "Pinned DST series",
                Start: eventStart,
                End: Zoned("2026-03-07T10:00:00", "America/New_York"),
                RecurrenceSet: new CalendarEventRecurrenceSetCreate(
                    Rule: "FREQ=DAILY;COUNT=4",
                    RecurrenceDates:
                    [
                        new CalendarRecurrenceDateCreate(Value: Zoned(
                            "2026-03-11T09:00:00",
                            "America/New_York"))
                    ],
                    ExceptionDates: [Zoned("2026-03-09T09:00:00", "America/New_York")],
                    Overrides:
                    [
                        new CalendarEventRecurrenceOverrideCreate(
                            eventOverrideIdentity,
                            CalendarRecurrenceOverrideStatus.Active,
                            new CalendarEventCreateFields(
                                Summary: "Paris override",
                                Start: Zoned("2026-03-08T15:00:00", "Europe/Paris"),
                                End: Zoned("2026-03-08T16:00:00", "Europe/Paris")))
                    ])));
        var todoRequest = new CalendarTodoCreateRequest(
            destination,
            "pinned-recurring-todo",
            new CalendarTodoCreateFields(
                Summary: "Pinned RDATE-only todo",
                Start: new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-17"),
                Due: new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-18"),
                RecurrenceSet: new CalendarTodoRecurrenceSetCreate(
                    RecurrenceDates:
                    [
                        new CalendarRecurrenceDateCreate(Value: new CalendarTemporalValue(
                            CalendarTemporalKind.Date,
                            "2026-08-24"))
                    ],
                    ExceptionDates: [new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-31")],
                    Overrides:
                    [
                        new CalendarTodoRecurrenceOverrideCreate(
                            new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-24"),
                            CalendarRecurrenceOverrideStatus.Cancelled,
                            new CalendarTodoCreateFields(
                                Summary: "Cancelled review",
                                Start: new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-24"),
                                Due: new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-25")))
                    ])));

        var createdEvent = await service.CreateEventAsync(eventRequest, TestContext.Current.CancellationToken);
        var createdTodo = await service.CreateTodoAsync(todoRequest, TestContext.Current.CancellationToken);

        createdEvent.Code.ShouldBe(CalendarEntityCreateCode.Success, DescribeCreateResult(createdEvent, requestTrace));
        createdTodo.Code.ShouldBe(CalendarEntityCreateCode.Success, DescribeCreateResult(createdTodo, requestTrace));
        var eventHref = AssertAuthoritativeCreate(
            createdEvent.Snapshot!,
            calendarHref,
            "pinned-recurring-event",
            "VEVENT");
        var todoHref = AssertAuthoritativeCreate(
            createdTodo.Snapshot!,
            calendarHref,
            "pinned-recurring-todo",
            "VTODO");
        var eventContent = System.Text.Encoding.UTF8.GetString(createdEvent.Snapshot!.AuthoritativeUtf8.Span);
        eventContent.ShouldContain("RRULE:FREQ=DAILY;COUNT=4");
        eventContent.ShouldContain("RDATE;TZID=America/New_York:20260311T090000");
        eventContent.ShouldContain("EXDATE;TZID=America/New_York:20260309T090000");
        eventContent.ShouldContain("RECURRENCE-ID;TZID=America/New_York:20260308T090000");
        eventContent.Split("BEGIN:VTIMEZONE", StringSplitOptions.None).Length.ShouldBe(3);
        eventContent.Split("UID:pinned-recurring-event", StringSplitOptions.None).Length.ShouldBe(3);
        var todoContent = System.Text.Encoding.UTF8.GetString(createdTodo.Snapshot!.AuthoritativeUtf8.Span);
        todoContent.ShouldNotContain("RRULE:");
        todoContent.ShouldContain("RDATE;VALUE=DATE:20260824");
        todoContent.ShouldContain("EXDATE;VALUE=DATE:20260831");
        todoContent.ShouldContain("RECURRENCE-ID;VALUE=DATE:20260824");
        todoContent.ShouldContain("STATUS:CANCELLED");
        todoContent.Split("UID:pinned-recurring-todo", StringSplitOptions.None).Length.ShouldBe(3);

        var putsBeforePeriod = requestTrace.Count(entry => entry.StartsWith("PUT:", StringComparison.Ordinal));
        var periodResult = await service.CreateEventAsync(
            new CalendarEventCreateRequest(
                destination,
                "pinned-period-event",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"),
                    RecurrenceSet: new CalendarEventRecurrenceSetCreate(
                        RecurrenceDates:
                        [
                            new CalendarRecurrenceDateCreate(Period: new CalendarRecurrencePeriodCreate(
                                new CalendarTemporalValue(
                                    CalendarTemporalKind.UtcDateTime,
                                    "2026-08-18T13:00:00Z"),
                                Duration: "PT1H"))
                        ]))),
            TestContext.Current.CancellationToken);

        periodResult.Code.ShouldBe(CalendarEntityCreateCode.UnsupportedCapability);
        periodResult.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        requestTrace.Count(entry => entry.StartsWith("PUT:", StringComparison.Ordinal)).ShouldBe(putsBeforePeriod);

        var exactBoundary = await service.CreateEventAsync(
            new CalendarEventCreateRequest(
                destination,
                "pinned-rrule-10000",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"),
                    RecurrenceSet: new CalendarEventRecurrenceSetCreate(
                        Rule: "FREQ=DAILY;COUNT=10000"))),
            TestContext.Current.CancellationToken);
        exactBoundary.Code.ShouldBe(
            CalendarEntityCreateCode.Success,
            DescribeCreateResult(exactBoundary, requestTrace));
        var exactBoundaryHref = AssertAuthoritativeCreate(
            exactBoundary.Snapshot!,
            calendarHref,
            "pinned-rrule-10000",
            "VEVENT");

        var putsBeforeOverflow = requestTrace.Count(entry => entry.StartsWith("PUT:", StringComparison.Ordinal));
        var overflowBoundary = await service.CreateEventAsync(
            new CalendarEventCreateRequest(
                destination,
                "pinned-rrule-10001",
                new CalendarEventCreateFields(
                    Start: new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-17T13:00:00Z"),
                    RecurrenceSet: new CalendarEventRecurrenceSetCreate(
                        Rule: "FREQ=DAILY;COUNT=10001"))),
            TestContext.Current.CancellationToken);
        overflowBoundary.Code.ShouldBe(CalendarEntityCreateCode.RecurrenceUnevaluable);
        overflowBoundary.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        requestTrace.Count(entry => entry.StartsWith("PUT:", StringComparison.Ordinal)).ShouldBe(putsBeforeOverflow);

        var unboundedZone = await service.CreateEventAsync(
            new CalendarEventCreateRequest(
                destination,
                "pinned-unbounded-zone",
                new CalendarEventCreateFields(
                    Start: Zoned("1990-01-15T09:00:00", "America/New_York"),
                    End: Zoned("1990-01-15T10:00:00", "America/New_York"),
                    RecurrenceSet: new CalendarEventRecurrenceSetCreate(
                        Rule: "FREQ=YEARLY",
                        RecurrenceDates:
                        [
                            new CalendarRecurrenceDateCreate(Value: Zoned(
                                "2090-07-15T09:00:00",
                                "America/New_York"))
                        ]))),
            TestContext.Current.CancellationToken);
        unboundedZone.Code.ShouldBe(
            CalendarEntityCreateCode.Success,
            DescribeCreateResult(unboundedZone, requestTrace));
        var unboundedZoneHref = AssertAuthoritativeCreate(
            unboundedZone.Snapshot!,
            calendarHref,
            "pinned-unbounded-zone",
            "VEVENT");
        var unboundedContent = System.Text.Encoding.UTF8.GetString(unboundedZone.Snapshot!.AuthoritativeUtf8.Span);
        unboundedContent.ShouldContain("RRULE:FREQ=YEARLY");
        unboundedContent.ShouldContain("RDATE:9998");

        var durationZone = await service.CreateEventAsync(
            new CalendarEventCreateRequest(
                destination,
                "pinned-duration-zone",
                new CalendarEventCreateFields(
                    Start: Zoned("2040-03-10T12:00:00", "America/New_York"),
                    Duration: "P1D",
                    RecurrenceSet: new CalendarEventRecurrenceSetCreate(
                        Rule: "FREQ=DAILY;COUNT=1"))),
            TestContext.Current.CancellationToken);
        durationZone.Code.ShouldBe(
            CalendarEntityCreateCode.Success,
            DescribeCreateResult(durationZone, requestTrace));
        var durationZoneHref = AssertAuthoritativeCreate(
            durationZone.Snapshot!,
            calendarHref,
            "pinned-duration-zone",
            "VEVENT");
        var durationContent = System.Text.Encoding.UTF8.GetString(durationZone.Snapshot!.AuthoritativeUtf8.Span);
        durationContent.ShouldContain("DURATION:P1D");
        durationContent.ShouldContain("20400311T020000");

        await fixture.DeleteResourceHrefAsync(
            eventHref,
            createdEvent.Snapshot.EntityTag,
            TestContext.Current.CancellationToken);
        await fixture.DeleteResourceHrefAsync(
            todoHref,
            createdTodo.Snapshot.EntityTag,
            TestContext.Current.CancellationToken);
        await fixture.DeleteResourceHrefAsync(
            exactBoundaryHref,
            exactBoundary.Snapshot!.EntityTag,
            TestContext.Current.CancellationToken);
        await fixture.DeleteResourceHrefAsync(
            unboundedZoneHref,
            unboundedZone.Snapshot.EntityTag,
            TestContext.Current.CancellationToken);
        await fixture.DeleteResourceHrefAsync(
            durationZoneHref,
            durationZone.Snapshot!.EntityTag,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pinned_profile_losslessly_patches_nonrecurring_event_with_exact_strong_revision()
    {
        var calendarHref = $"{fixture.BaseUrl}/conformance/conformance/";
        const string resourceName = "pinned-patch-event.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\nBEGIN:VEVENT\r\nUID:pinned-patch-event\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260818T130000Z\r\nSUMMARY:Before\r\nX-KEEP;X-DUP=One,one,TWO:opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var seeded = await fixture.SeedResourceAsync(
            resourceName,
            content,
            TestContext.Current.CancellationToken);
        var href = calendarHref + resourceName;
        await using var provider = CreateProvider(fixture.BaseUrl, calendarHref);
        var service = provider.GetRequiredService<ICalendarService>();
        var before = await service.GetResourceAsync(href, TestContext.Current.CancellationToken);
        before.Snapshot.ShouldNotBeNull();

        var result = await service.PatchEventAsync(
            new CalendarEventPatchRequest(
                new CalendarResourceRevisionReference(
                    href,
                    "pinned-patch-event",
                    CalendarEntityKind.Event,
                    before.Snapshot.EntityTag),
                new CalendarMutationTarget("master"),
                new CalendarEventPatch(
                    Summary: new(CalendarScalarPatchOperation.Set, "After"),
                    Categories: new(CalendarCollectionPatchOperation.AddRemove, Add: ["Pinned"]))),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot!.Projection.Summary.ShouldBe("After");
        var preservedParameter = result.Snapshot.CalendarProperties.Single(property => property.Name == "X-KEEP")
            .Parameters.ShouldHaveSingleItem();
        preservedParameter.Name.ShouldBe("X-DUP");
        preservedParameter.Values.ShouldBe(["One", "one", "TWO"]);
        result.Snapshot.CalendarProperties.Single(property => property.Name == "CATEGORIES")
            .RawEncodedValue.ShouldBe("Pinned");

        await fixture.DeleteResourceHrefAsync(href, result.Snapshot.EntityTag, TestContext.Current.CancellationToken);
        seeded.EntityTag.ShouldNotBe(result.Snapshot.EntityTag);
    }

    [Fact]
    public async Task Pinned_profile_patches_one_range_affected_occurrence_as_complete_individual_override()
    {
        var calendarHref = $"{fixture.BaseUrl}/conformance/conformance/";
        const string resourceName = "pinned-patch-one-occurrence.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\nBEGIN:VEVENT\r\nUID:pinned-patch-one-occurrence\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260818T130000Z\r\nDTEND:20260818T140000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:pinned-patch-one-occurrence\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260819T130000Z\r\nDTSTART:20260819T150000Z\r\nDTEND:20260819T170000Z\r\nSUMMARY:Range\r\nX-KEEP:range-opaque\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var seeded = await fixture.SeedResourceAsync(resourceName, content, TestContext.Current.CancellationToken);
        var href = calendarHref + resourceName;
        var requestTrace = new ConcurrentQueue<string>();
        await using var provider = CreateProvider(fixture.BaseUrl, calendarHref, requestTrace);
        var service = provider.GetRequiredService<ICalendarService>();

        var result = await service.PatchEventAsync(
            new CalendarEventPatchRequest(
                new CalendarResourceRevisionReference(
                    href,
                    "pinned-patch-one-occurrence",
                    CalendarEntityKind.Event,
                    seeded.EntityTag),
                new CalendarMutationTarget(
                    "one-occurrence",
                    new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T13:00:00Z")),
                new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Patched individual"))),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot!.EntityTag.ShouldNotBe(seeded.EntityTag);
        requestTrace.Count(entry => entry.StartsWith("PUT:", StringComparison.Ordinal)).ShouldBe(1);
        var individual = result.Snapshot.CalendarProperties.Where(property =>
            property.ComponentPath[^1].Name == "VEVENT" && property.ComponentPath[^1].Occurrence == 2).ToArray();
        individual.Single(property => property.Name == "UID").RawEncodedValue.ShouldBe("pinned-patch-one-occurrence");
        individual.Single(property => property.Name == "RECURRENCE-ID").RawEncodedValue.ShouldBe("20260821T130000Z");
        individual.Single(property => property.Name == "DTSTART").RawEncodedValue.ShouldBe("20260821T150000Z");
        individual.Single(property => property.Name == "DTEND").RawEncodedValue.ShouldBe("20260821T170000Z");
        individual.Single(property => property.Name == "SUMMARY").RawEncodedValue.ShouldBe("Patched individual");
        individual.Single(property => property.Name == "X-KEEP").RawEncodedValue.ShouldBe("range-opaque");

        await fixture.DeleteResourceHrefAsync(href, result.Snapshot.EntityTag, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pinned_profile_completes_one_todo_occurrence_with_one_exact_conditional_put()
    {
        var calendarHref = $"{fixture.BaseUrl}/conformance/conformance/";
        const string resourceName = "pinned-todo-completion.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\nBEGIN:VTODO\r\nUID:pinned-todo-completion\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260818T090000Z\r\nDUE:20260818T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Completion series\r\nX-KEEP:opaque\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var seeded = await fixture.SeedResourceAsync(resourceName, content, TestContext.Current.CancellationToken);
        var href = calendarHref + resourceName;
        var requestTrace = new ConcurrentQueue<string>();
        await using var provider = CreateProvider(fixture.BaseUrl, calendarHref, requestTrace);
        var service = provider.GetRequiredService<ICalendarService>();

        var result = await service.CompleteTodoAsync(
            new CalendarTodoCompletionRequest(
                new CalendarResourceRevisionReference(
                    href,
                    "pinned-todo-completion",
                    CalendarEntityKind.Todo,
                    seeded.EntityTag),
                new CalendarTemporalValue(
                    CalendarTemporalKind.UtcDateTime,
                    "2026-08-19T09:00:00Z")),
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarEntityPatchCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot!.EntityTag.ShouldNotBe(seeded.EntityTag);
        requestTrace.Count(entry => entry.StartsWith("PUT:", StringComparison.Ordinal)).ShouldBe(1);
        result.Snapshot.CalendarProperties.Count(property => property.Name == "STATUS"
            && property.RawEncodedValue == "COMPLETED").ShouldBe(1);
        result.Snapshot.CalendarProperties.Count(property => property.Name == "PERCENT-COMPLETE"
            && property.RawEncodedValue == "100").ShouldBe(1);
        result.Snapshot.CalendarProperties.Count(property => property.Name == "COMPLETED").ShouldBe(1);
        result.Snapshot.CalendarProperties.Single(property => property.Name == "RECURRENCE-ID")
            .RawEncodedValue.ShouldBe("20260819T090000Z");
        result.Snapshot.CalendarProperties.Where(property => property.Name == "X-KEEP")
            .Select(property => property.RawEncodedValue).ShouldBe(["opaque", "opaque"]);

        await fixture.DeleteResourceHrefAsync(href, result.Snapshot.EntityTag, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pinned_profile_applies_entire_event_and_range_todo_with_one_put_each()
    {
        var calendarHref = $"{fixture.BaseUrl}/conformance/conformance/";
        const string eventName = "pinned-patch-entire-event.ics";
        const string eventContent = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\nBEGIN:VEVENT\r\nUID:pinned-entire-event\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260818T130000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master\r\nX-MASTER:keep\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:pinned-entire-event\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260819T130000Z\r\nDTSTART:20260819T150000Z\r\nSUMMARY:Override\r\nX-OVERRIDE:keep\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var eventSeed = await fixture.SeedResourceAsync(
            eventName,
            eventContent,
            TestContext.Current.CancellationToken);
        var eventHref = calendarHref + eventName;
        var eventTrace = new ConcurrentQueue<string>();
        await using var eventProvider = CreateProvider(fixture.BaseUrl, calendarHref, eventTrace);
        var eventService = eventProvider.GetRequiredService<ICalendarService>();

        var eventResult = await eventService.PatchEventAsync(
            new CalendarEventPatchRequest(
                new CalendarResourceRevisionReference(
                    eventHref,
                    "pinned-entire-event",
                    CalendarEntityKind.Event,
                    eventSeed.EntityTag),
                new CalendarMutationTarget("entire-set"),
                new CalendarEventPatch(Summary: new(CalendarScalarPatchOperation.Set, "Entire"))),
            TestContext.Current.CancellationToken);

        eventResult.Code.ShouldBe(CalendarEntityPatchCode.Success);
        eventResult.Snapshot!.CalendarProperties.Count(property =>
            property.Name == "SUMMARY" && property.RawEncodedValue == "Entire").ShouldBe(2);
        eventResult.Snapshot.CalendarProperties.Single(property => property.Name == "X-MASTER")
            .RawEncodedValue.ShouldBe("keep");
        eventResult.Snapshot.CalendarProperties.Single(property => property.Name == "X-OVERRIDE")
            .RawEncodedValue.ShouldBe("keep");
        eventTrace.Count(entry => entry.StartsWith("PUT:", StringComparison.Ordinal)).ShouldBe(1);

        const string todoName = "pinned-patch-range-todo.ics";
        const string todoContent = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\nBEGIN:VTODO\r\nUID:pinned-range-todo\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260818T090000Z\r\nDUE:20260818T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Master\r\nEND:VTODO\r\nBEGIN:VTODO\r\nUID:pinned-range-todo\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260820T090000Z\r\nDTSTART:20260820T110000Z\r\nDUE:20260820T120000Z\r\nSUMMARY:Individual\r\nX-TODO:keep\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var todoSeed = await fixture.SeedResourceAsync(
            todoName,
            todoContent,
            TestContext.Current.CancellationToken);
        var todoHref = calendarHref + todoName;
        var todoTrace = new ConcurrentQueue<string>();
        await using var todoProvider = CreateProvider(fixture.BaseUrl, calendarHref, todoTrace);
        var todoService = todoProvider.GetRequiredService<ICalendarService>();

        var todoResult = await todoService.PatchTodoAsync(
            new CalendarTodoPatchRequest(
                new CalendarResourceRevisionReference(
                    todoHref,
                    "pinned-range-todo",
                    CalendarEntityKind.Todo,
                    todoSeed.EntityTag),
                new CalendarMutationTarget(
                    "this-and-future",
                    new CalendarTemporalValue(
                        CalendarTemporalKind.UtcDateTime,
                        "2026-08-19T09:00:00Z")),
                new CalendarTodoPatch(Summary: new(CalendarScalarPatchOperation.Set, "Future"))),
            TestContext.Current.CancellationToken);

        todoResult.Code.ShouldBe(CalendarEntityPatchCode.Success);
        todoResult.Snapshot!.CalendarProperties.Count(property =>
            property.Name == "SUMMARY" && property.RawEncodedValue == "Future").ShouldBe(2);
        todoResult.Snapshot.CalendarProperties.Single(property => property.Name == "RECURRENCE-ID"
                && property.Parameters.Any(parameter => parameter.Name == "RANGE"))
            .RawEncodedValue.ShouldBe("20260819T090000Z");
        todoResult.Snapshot.CalendarProperties.Single(property => property.Name == "X-TODO")
            .RawEncodedValue.ShouldBe("keep");
        todoTrace.Count(entry => entry.StartsWith("PUT:", StringComparison.Ordinal)).ShouldBe(1);

        await fixture.DeleteResourceHrefAsync(
            eventHref,
            eventResult.Snapshot.EntityTag,
            TestContext.Current.CancellationToken);
        await fixture.DeleteResourceHrefAsync(
            todoHref,
            todoResult.Snapshot.EntityTag,
            TestContext.Current.CancellationToken);
    }

    internal static ServiceProvider CreateProvider(
        string baseUrl,
        string calendarHref,
        ConcurrentQueue<string>? requestTrace = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavTasks(options =>
        {
            options.BaseUrl = baseUrl;
            options.CalendarHrefs = calendarHref;
            options.Username = ConformanceUsername;
            options.Password = ConformancePassword;
        });
        if (requestTrace is not null)
            services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new SafeRequestTraceFilter(requestTrace));
        return services.BuildServiceProvider();
    }

    private async Task<ObservedResource> PutAndGetAsync(
        string calendarHref,
        string name,
        string content)
    {
        var href = $"{calendarHref}{name}";
        var observed = await fixture.SeedResourceAsync(
            name,
            content,
            TestContext.Current.CancellationToken);
        return new ObservedResource(name, href, observed.EntityTag, observed.Utf8);
    }

    private Task DeleteAsync(
        ObservedResource resource,
        CancellationToken cancellationToken) =>
        fixture.DeleteResourceAsync(resource.Name, resource.EntityTag, cancellationToken);

    private static string Event(string uid, string temporalLines) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\nBEGIN:VEVENT\r\n"
        + $"UID:{uid}\r\nDTSTAMP:20260815T120000Z\r\n{temporalLines}END:VEVENT\r\nEND:VCALENDAR\r\n";

    private static string RangeEvent() =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:range\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260814T090000Z\r\n"
        + "DURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=5\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:range\r\nDTSTAMP:20260815T120000Z\r\n"
        + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260816T090000Z\r\n"
        + "DTSTART:20260816T110000Z\r\nDURATION:PT1H\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:range\r\nDTSTAMP:20260815T120000Z\r\n"
        + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260817T090000Z\r\n"
        + "DTSTART:20260817T130000Z\r\nDURATION:PT2H\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private static CalendarTemporalValue Zoned(string value, string timeZoneId) =>
        new(CalendarTemporalKind.ZonedDateTime, value, timeZoneId);

    private static string AssertAuthoritativeCreate(
        CalendarResourceSnapshot snapshot,
        string calendarHref,
        string uid,
        string component)
    {
        AssertCanonicalDirectChild(calendarHref, snapshot.ResourceHref);
        snapshot.EntityTag.ShouldStartWith("\"");
        snapshot.EntityTag.ShouldEndWith("\"");
        snapshot.Projection.EntityUid.ShouldBe(uid);
        var content = System.Text.Encoding.UTF8.GetString(snapshot.AuthoritativeUtf8.Span);
        content.ShouldContain($"BEGIN:{component}");
        content.ShouldContain($"UID:{uid}");
        return snapshot.ResourceHref;
    }

    private static void AssertLosslessProperty(
        CalendarResourceSnapshot snapshot,
        string component,
        string propertyName,
        CalendarPropertyValueType valueType,
        string rawEncodedValue,
        params CalendarParameter[] expectedParameters)
    {
        var property = snapshot.CalendarProperties.Single(candidate =>
            candidate.ComponentPath[^1].Name.Equals(component, StringComparison.OrdinalIgnoreCase)
            && candidate.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
        property.ValueType.ShouldBe(valueType);
        property.RawEncodedValue.ShouldBe(rawEncodedValue);
        property.Parameters.Select(CanonicalParameter).Order(StringComparer.Ordinal).ToArray()
            .ShouldBe(expectedParameters.Select(CanonicalParameter).Order(StringComparer.Ordinal).ToArray());
    }

    private static string CanonicalParameter(CalendarParameter parameter) => JsonSerializer.Serialize(new
    {
        Name = parameter.Name.ToUpperInvariant(),
        Values = parameter.Values
    });

    private static string DescribeCreateResult(
        CalendarEntityCreateResult result,
        IEnumerable<string> requestTrace) => JsonSerializer.Serialize(new
        {
            Code = result.Code.ToString(),
            MutationState = result.MutationState.ToString(),
            SnapshotPresent = result.Snapshot is not null,
            DiagnosticCodes = result.Snapshot?.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray() ?? [],
            RequestTrace = requestTrace.ToArray()
        });

    private static void AssertCanonicalDirectChild(string calendarHref, string resourceHref)
    {
        var calendar = new Uri(calendarHref, UriKind.Absolute);
        var resource = new Uri(resourceHref, UriKind.Absolute);
        resource.GetLeftPart(UriPartial.Authority).ShouldBe(calendar.GetLeftPart(UriPartial.Authority));
        resource.Query.ShouldBeEmpty();
        resource.Fragment.ShouldBeEmpty();
        resource.UserInfo.ShouldBeEmpty();
        resourceHref.ShouldNotContain("%2F", Case.Insensitive);
        resourceHref.ShouldNotContain("%5C", Case.Insensitive);
        var relative = calendar.MakeRelativeUri(resource).OriginalString;
        relative.ShouldNotBeEmpty();
        relative.ShouldNotContain("/");
        relative.ShouldNotContain("\\");
        new Uri(calendar, relative).AbsoluteUri.ShouldBe(resource.AbsoluteUri);
    }

    private sealed record ObservedResource(string Name, string Href, string EntityTag, byte[] Utf8);

    private sealed class SafeRequestTraceFilter(ConcurrentQueue<string> trace) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.AdditionalHandlers.Insert(0, new SafeRequestTraceHandler(trace));
        };
    }

    private sealed class SafeRequestTraceHandler(ConcurrentQueue<string> trace) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                trace.Enqueue($"{request.Method.Method}:{(int)response.StatusCode}");
                return response;
            }
            catch (Exception exception) when (exception is HttpRequestException
                or IOException
                or OperationCanceledException)
            {
                trace.Enqueue($"{request.Method.Method}:{exception.GetType().Name}");
                throw;
            }
        }
    }
}

public sealed class RadicaleConformanceHarnessConfigurationTests
{
    [Fact]
    public void Provider_resolves_calendar_service_with_nonempty_conformance_credentials()
    {
        using var provider = RadicaleConformanceHarnessTests.CreateProvider(
            "http://localhost:5232",
            "http://localhost:5232/conformance/conformance/");

        RadicaleConformanceFixture.Username.ShouldNotBeNullOrWhiteSpace();
        RadicaleConformanceFixture.Password.ShouldNotBeNullOrWhiteSpace();
        provider.GetRequiredService<ICalendarService>().ShouldNotBeNull();
    }
}
