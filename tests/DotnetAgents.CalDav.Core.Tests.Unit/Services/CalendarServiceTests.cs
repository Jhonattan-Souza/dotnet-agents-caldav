using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarServiceTests
{
    [Fact]
    public async Task QueryEntitiesAsync_DefaultScopeUsesOnlyTheRequestedIndependentDefault()
    {
        const string eventCalendar = "https://cal.example/events/";
        const string todoCalendar = "https://cal.example/todos/";
        const string eventHref = "https://cal.example/events/standup.ics";
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            DefaultEventCalendarName = "Events",
            DefaultTodoCalendarName = "To-dos"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            EntityCalendar(eventCalendar, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised),
            EntityCalendar(todoCalendar, "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)
        ]);
        client.QueryCalendarResourceHrefsAsync(eventCalendar, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([eventHref]);
        client.GetCalendarResourceAsync(eventHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(eventHref, "\"r1\"", Event("event-1", "20260816T090000Z", "Standup")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.Select(item => item.ResourceHref).ShouldBe([eventHref]);
        await client.Received(1).QueryCalendarResourceHrefsAsync(
            eventCalendar,
            CalendarEntityKind.Event,
            null,
            null,
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().QueryCalendarResourceHrefsAsync(
            todoCalendar,
            Arg.Any<CalendarEntityKind>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryEntitiesAsync_DefaultScopeUnionsDistinctEventAndTodoDefaults()
    {
        const string eventCalendar = "https://cal.example/events/";
        const string todoCalendar = "https://cal.example/todos/";
        const string eventHref = "https://cal.example/events/standup.ics";
        const string todoHref = "https://cal.example/todos/report.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                DefaultEventCalendarName = "Events",
                DefaultTodoCalendarName = "To-dos"
            }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            EntityCalendar(eventCalendar, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised),
            EntityCalendar(todoCalendar, "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)
        ]);
        client.QueryCalendarResourceHrefsAsync(eventCalendar, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([eventHref]);
        client.QueryCalendarResourceHrefsAsync(todoCalendar, CalendarEntityKind.Todo, null, null, Arg.Any<CancellationToken>())
            .Returns([todoHref]);
        client.GetCalendarResourceAsync(eventHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(eventHref, "\"e1\"", Event("event-1", "20260816T090000Z", "Standup")));
        client.GetCalendarResourceAsync(todoHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(todoHref, "\"t1\"", Todo("todo-1", "Report")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Todo, CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.Select(item => item.ResourceHref).ShouldBe([eventHref, todoHref]);
    }

    [Fact]
    public async Task QueryEntitiesAsync_SelectedNameDoesNotBroadenWhenKindIsNotAdvertised()
    {
        const string selectedCalendar = "https://cal.example/selected/";
        const string otherCalendar = "https://cal.example/other/";
        const string resourceHref = "https://cal.example/selected/event.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            EntityCalendar(selectedCalendar, " Selected ", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised),
            EntityCalendar(otherCalendar, "Other", EntityKindSupport.Advertised, EntityKindSupport.Advertised)
        ]);
        client.QueryCalendarResourceHrefsAsync(selectedCalendar, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", Event("event-1", "20260816T090000Z", "Selected event")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(
                CalendarEntityScope.Selected(new CalendarReference(Name: "selected")),
                [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.Select(item => item.ResourceHref).ShouldBe([resourceHref]);
        result.Diagnostics.Select(item => item.Code).ShouldBe(["entity_kind_not_advertised"]);
        await client.DidNotReceive().QueryCalendarResourceHrefsAsync(
            otherCalendar,
            Arg.Any<CalendarEntityKind>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryEntitiesAsync_SelectedNameWithSurroundingWhitespaceIsInvalidBeforeDiscovery()
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(
                CalendarEntityScope.Selected(new CalendarReference(Name: "  Work  ")),
                [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.InvalidInput);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryEntitiesAsync_UndefinedEntityKindIsInvalidBeforeDiscovery()
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.All, [(CalendarEntityKind)999]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.InvalidInput);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryEntitiesAsync_SelectedNameDoesNotRevealOutOfScopeExistence()
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
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            EntityCalendar("https://cal.example/authorized/", "Authorized", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised),
            EntityCalendar("https://cal.example/private/", "Private", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)
        ]);

        var existingOutsideScope = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(
                CalendarEntityScope.Selected(new CalendarReference(Name: "Private")),
                [CalendarEntityKind.Event]),
            CancellationToken.None);
        var nonexistent = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(
                CalendarEntityScope.Selected(new CalendarReference(Name: "Missing")),
                [CalendarEntityKind.Event]),
            CancellationToken.None);

        existingOutsideScope.Code.ShouldBe(CalendarEntityQueryCode.NotFound);
        nonexistent.Code.ShouldBe(CalendarEntityQueryCode.NotFound);
        existingOutsideScope.AuthorizedCandidates.ShouldBe(nonexistent.AuthorizedCandidates);
    }

    [Fact]
    public async Task QueryEntitiesAsync_ExplicitAllUsesOnlyCompatibleOrUnknownCalendarsAndDeduplicatesCandidates()
    {
        const string advertised = "https://cal.example/a/";
        const string unknown = "https://cal.example/b/";
        const string excluded = "https://cal.example/c/";
        const string firstHref = "https://cal.example/a/shared.ics";
        const string secondHref = "https://cal.example/b/second.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            EntityCalendar(excluded, "Excluded", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised),
            EntityCalendar(unknown, "Unknown", EntityKindSupport.Unknown, EntityKindSupport.Advertised),
            EntityCalendar(advertised, "Advertised", EntityKindSupport.Advertised, EntityKindSupport.Advertised)
        ]);
        client.QueryCalendarResourceHrefsAsync(advertised, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([firstHref, firstHref]);
        client.QueryCalendarResourceHrefsAsync(unknown, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([secondHref]);
        client.GetCalendarResourceAsync(firstHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(firstHref, "\"r1\"", Event("event-1", "20260816T090000Z", "First")));
        client.GetCalendarResourceAsync(secondHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(secondHref, "\"r2\"", Event("event-2", "20260816T100000Z", "Second")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.All, [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.Select(item => item.ResourceHref).ShouldBe([firstHref, secondHref]);
        await client.Received(1).GetCalendarResourceAsync(firstHref, Arg.Any<CancellationToken>());
        await client.DidNotReceive().QueryCalendarResourceHrefsAsync(
            excluded,
            CalendarEntityKind.Event,
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryEntitiesAsync_CarriesSafeConfiguredScopeDiagnostics()
    {
        const string calendarHref = "https://cal.example/events/";
        const string missingHref = "https://cal.example/missing/";
        const string resourceHref = "https://cal.example/events/a.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                CalendarHrefs = $"{calendarHref},{calendarHref},{missingHref}"
            }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>())
            .Returns(CalendarResourceRead.Success(resourceHref, "\"r1\"", Event("a", "20260816T100000Z", "A")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.All, [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Diagnostics.Select(item => item.Code)
            .ShouldBe(["duplicate_calendar_href", "calendar_href_not_found"]);
        result.Diagnostics.ShouldAllBe(item => !item.Message.Contains(calendarHref, StringComparison.Ordinal)
            && !item.Message.Contains(missingHref, StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryEntitiesAsync_ResourceLimitReturnsNoPartialItemsBeforeAnyGet()
    {
        const string calendarHref = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                DefaultEventCalendarName = "Events"
            }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, 5001).Select(index => $"{calendarHref}{index:D4}.ics").ToArray());

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.LimitExhausted);
        result.Items.ShouldBeEmpty();
        await client.DidNotReceive().GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryEntitiesAsync_ReturnsMixedCandidateAsOpaqueInsteadOfPartialOrSilentNonMatch()
    {
        const string calendarHref = "https://cal.example/mixed/";
        const string resourceHref = "https://cal.example/mixed/mixed.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Mixed" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Mixed", EntityKindSupport.Advertised, EntityKindSupport.Advertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", Mixed("event-1", "todo-1")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldHaveSingleItem().Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldContain("opaque_filter_unresolved");
        result.Items[0].Diagnostics.Select(item => item.Code).ShouldBe(["mixed_entity_kinds"]);
    }

    [Fact]
    public async Task QueryEntitiesAsync_EventWindowUsesHalfOpenOverlapBoundariesLocally()
    {
        const string calendarHref = "https://cal.example/events/";
        var endingAtFrom = $"{calendarHref}ending.ics";
        var overlapping = $"{calendarHref}overlap.ics";
        var startingAtTo = $"{calendarHref}starting.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        var from = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-16T11:00:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, from, to, Arg.Any<CancellationToken>())
            .Returns([endingAtFrom, overlapping, startingAtTo]);
        client.GetCalendarResourceAsync(endingAtFrom, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(endingAtFrom, "\"r1\"", EventWithEnd("ending", "20260816T090000Z", "20260816T100000Z")));
        client.GetCalendarResourceAsync(overlapping, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(overlapping, "\"r2\"", EventWithEnd("overlap", "20260816T095959Z", "20260816T100001Z")));
        client.GetCalendarResourceAsync(startingAtTo, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(startingAtTo, "\"r3\"", EventWithEnd("starting", "20260816T110000Z", "20260816T120000Z")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event], from, to),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.Select(item => item.ResourceHref).ShouldBe([overlapping]);
    }

    [Fact]
    public async Task QueryEntitiesAsync_TodoWindowUsesDuePointAndSpanBoundariesLocally()
    {
        const string calendarHref = "https://cal.example/todos/";
        var dueAtFrom = $"{calendarHref}due-from.ics";
        var dueAtTo = $"{calendarHref}due-to.ics";
        var spanEndingAtFrom = $"{calendarHref}span-ending.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultTodoCalendarName = "To-dos" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)]);
        var from = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-16T11:00:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo, from, to, Arg.Any<CancellationToken>())
            .Returns([dueAtFrom, dueAtTo, spanEndingAtFrom]);
        client.GetCalendarResourceAsync(dueAtFrom, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(dueAtFrom, "\"r1\"", TodoWithTiming("due-from", null, "20260816T100000Z")));
        client.GetCalendarResourceAsync(dueAtTo, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(dueAtTo, "\"r2\"", TodoWithTiming("due-to", null, "20260816T110000Z")));
        client.GetCalendarResourceAsync(spanEndingAtFrom, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(spanEndingAtFrom, "\"r3\"", TodoWithTiming("span", "20260816T090000Z", "20260816T100000Z")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Todo], from, to),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.Select(item => item.ResourceHref).ShouldBe([dueAtFrom]);
    }

    [Theory]
    [InlineData("20260816", "VALUE", "DATE")]
    [InlineData("20260816T100000", null, null)]
    [InlineData("20260816T100000", "TZID", "Mars/Olympus")]
    public async Task QueryEntitiesAsync_UnresolvedTemporalKindsRemainVisibleWithDiagnostic(
        string rawStart,
        string? parameterName,
        string? parameterValue)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/unresolved.ics";
        var parameter = parameterName is null ? string.Empty : $";{parameterName}={parameterValue}";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        var from = DateTimeOffset.Parse("2026-08-16T09:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-16T11:00:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, from, to, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", EventWithRawStart("unresolved", parameter, rawStart)));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event], from, to),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_EvaluatesUtcEventRecurrenceAndExclusionsLocally()
    {
        const string calendarHref = "https://cal.example/events/";
        var included = $"{calendarHref}included.ics";
        var excluded = $"{calendarHref}excluded.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        var from = DateTimeOffset.Parse("2026-08-16T10:30:00Z");
        var to = DateTimeOffset.Parse("2026-08-16T10:45:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, from, to, Arg.Any<CancellationToken>())
            .Returns([included, excluded]);
        client.GetCalendarResourceAsync(included, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(included, "\"r1\"", RecurringEvent("included", null)));
        client.GetCalendarResourceAsync(excluded, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(excluded, "\"r2\"", RecurringEvent("excluded", "20260816T100000Z")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event], from, to),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.Select(item => item.ResourceHref).ShouldBe([included]);
    }

    [Fact]
    public async Task QueryEntitiesAsync_EvaluatesUtcTodoRecurrenceEffectiveSpanLocally()
    {
        const string calendarHref = "https://cal.example/todos/";
        var resourceHref = $"{calendarHref}recurring.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultTodoCalendarName = "To-dos" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)]);
        var from = DateTimeOffset.Parse("2026-08-16T10:15:00Z");
        var to = DateTimeOffset.Parse("2026-08-16T10:20:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo, from, to, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", RecurringTodo("todo")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Todo], from, to),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(2000, CalendarEntityQueryCode.Success, 1, null)]
    [InlineData(2001, CalendarEntityQueryCode.LimitExhausted, 0, 2001)]
    public async Task QueryEntitiesAsync_EnforcesExactPerEntityOccurrenceBoundary(
        int occurrenceCount,
        CalendarEntityQueryCode expectedCode,
        int expectedItems,
        int? expectedObservedCount)
    {
        const string calendarHref = "https://cal.example/events/";
        var resourceHref = $"{calendarHref}recurring.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        var from = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-15T13:00:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, from, to, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", RecurringEventWithRule(
                "recurring", $"RRULE:FREQ=SECONDLY;COUNT={occurrenceCount}\r\n")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event], from, to),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.Items.Count.ShouldBe(expectedItems);
        result.Limits?.OccurrenceCount.ShouldBe(expectedObservedCount);
    }

    [Theory]
    [InlineData(5000, CalendarEntityQueryCode.Success, 3, null)]
    [InlineData(5001, CalendarEntityQueryCode.LimitExhausted, 0, 5001)]
    public async Task QueryEntitiesAsync_EnforcesExactTotalOccurrenceBoundary(
        int totalOccurrences,
        CalendarEntityQueryCode expectedCode,
        int expectedItems,
        int? expectedObservedCount)
    {
        var result = await QueryRecurringCountsAsync(
            2000,
            2000,
            totalOccurrences - 4000);

        result.Code.ShouldBe(expectedCode);
        result.Items.Count.ShouldBe(expectedItems);
        result.Limits?.OccurrenceCount.ShouldBe(expectedObservedCount);
    }

    [Theory]
    [InlineData("20531231T100000Z", CalendarEntityQueryCode.Success)]
    [InlineData("20540101T100000Z", CalendarEntityQueryCode.LimitExhausted)]
    public async Task QueryEntitiesAsync_EnforcesExactUnmatchedIncrementBoundaryWithoutFabricatingOccurrenceCount(
        string until,
        CalendarEntityQueryCode expectedCode)
    {
        var result = await QuerySingleEventAsync(
            $"DTSTART:20260816T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;BYMONTH=2;BYMONTHDAY=30;UNTIL={until}\r\n",
            "2026-08-16T10:00:00Z",
            "2026-08-16T11:00:00Z");

        result.Code.ShouldBe(expectedCode);
        result.Items.ShouldBeEmpty();
        result.Limits.ShouldBeNull();
    }

    [Fact]
    public async Task QueryEntitiesAsync_MixedOpaqueResourceRemainsTruthfulUnderTemporalFilter()
    {
        const string calendarHref = "https://cal.example/events/";
        var resourceHref = $"{calendarHref}opaque.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        var from = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-15T13:00:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, from, to, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", Mixed("event", "todo")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event], from, to),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldHaveSingleItem().Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Diagnostics.Select(item => item.Code).ShouldContain("opaque_filter_unresolved");
    }

    [Fact]
    public async Task QueryEntitiesAsync_ProjectableMultipleRRuleIsListableButWindowIsUnevaluable()
    {
        const string calendarHref = "https://cal.example/events/";
        var resourceHref = $"{calendarHref}multiple.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        var from = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-15T13:00:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, from, to, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", RecurringEventWithRule(
                "multiple", "RRULE:FREQ=DAILY;COUNT=3\r\nRRULE:FREQ=WEEKLY;COUNT=3\r\n")));

        var windowed = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event], from, to),
            CancellationToken.None);
        var listed = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            CancellationToken.None);

        windowed.Code.ShouldBe(CalendarEntityQueryCode.RecurrenceUnevaluable);
        windowed.Items.ShouldBeEmpty();
        listed.Code.ShouldBe(CalendarEntityQueryCode.Success);
        listed.Items.ShouldHaveSingleItem().Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        listed.Items[0].CalendarProperties.Count(property => property.Name == "RRULE").ShouldBe(2);
    }

    [Fact]
    public async Task QueryEntitiesAsync_RRuleOnOverrideIsUnevaluable()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART:20260816T100000Z\r\n"
            + "END:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:temporal\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260817T100000Z\r\nDTSTART:20260817T110000Z\r\n"
            + "RRULE:FREQ=DAILY;COUNT=2\r\n",
            "2026-08-16T10:00:00Z",
            "2026-08-18T12:00:00Z");

        result.Code.ShouldBe(CalendarEntityQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_RRuleOnMasterAndOverrideIsUnevaluable()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART:20260816T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:temporal\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260817T100000Z\r\nDTSTART:20260817T110000Z\r\n"
            + "RRULE:FREQ=DAILY;COUNT=2\r\n",
            "2026-08-16T10:00:00Z",
            "2026-08-18T12:00:00Z");

        result.Code.ShouldBe(CalendarEntityQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_ThisAndFutureOverrideIsListableButWindowIsUnevaluable()
    {
        const string bytesText = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nX-UNRELATED:keep\r\n"
            + "BEGIN:VTIMEZONE\r\nTZID:Custom/Office\r\nBEGIN:STANDARD\r\nDTSTART:20200101T000000\r\n"
            + "TZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
            + "BEGIN:VEVENT\r\nUID:ranged\r\nDTSTAMP:20260815T120000Z\r\nDTSTART;TZID=Custom/Office:20260816T100000\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:ranged\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE;TZID=Custom/Office:20260817T100000\r\n"
            + "DTSTART;TZID=Custom/Office:20260817T110000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/ranged.ics";
        var bytes = System.Text.Encoding.UTF8.GetBytes(bytesText);
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>())
            .Returns(CalendarResourceRead.Success(resourceHref, "\"r1\"", bytes));

        var windowed = await sut.QueryEntitiesAsync(new CalendarEntityQuery(
            CalendarEntityScope.Default,
            [CalendarEntityKind.Event],
            DateTimeOffset.Parse("2026-08-17T09:00:00Z"),
            DateTimeOffset.Parse("2026-08-17T10:00:00Z")), CancellationToken.None);
        var listed = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            CancellationToken.None);

        windowed.Code.ShouldBe(CalendarEntityQueryCode.RecurrenceUnevaluable);
        windowed.Items.ShouldBeEmpty();
        listed.Code.ShouldBe(CalendarEntityQueryCode.Success);
        listed.Items.ShouldHaveSingleItem().AuthoritativeUtf8.ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task QueryEntitiesAsync_ExdateOnlyRecurrenceSuppressesTheMasterStart()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART:20260816T100000Z\r\nEXDATE:20260816T100000Z\r\n",
            "2026-08-16T10:00:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_CompleteOverrideUsesItsMovedEffectiveSpanWithoutRule()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART:20260816T060000Z\r\nDURATION:PT30M\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:temporal\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260816T060000Z\r\nDTSTART:20260816T093000Z\r\nDURATION:PT1H\r\n",
            "2026-08-16T10:00:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task QueryEntitiesAsync_CommaSeparatedPeriodAndExdateRemainEvaluable()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART:20260816T060000Z\r\nDURATION:PT30M\r\n"
            + "RDATE;VALUE=PERIOD:20260816T070000Z/20260816T073000Z,20260816T080000Z/PT2H30M\r\n"
            + "EXDATE:20260816T060000Z,20260817T060000Z\r\n",
            "2026-08-16T10:00:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task QueryEntitiesAsync_ResolvesRecognizedIanaZoneAcrossDst()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART;TZID=Europe/Zurich:20260329T033000\r\nDURATION:PT30M\r\n",
            "2026-03-29T01:30:00Z",
            "2026-03-29T01:31:00Z");

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("20260329T023000")]
    [InlineData("20261025T023000")]
    public async Task QueryEntitiesAsync_IanaGapOrOverlapIsUnresolved(string localStart)
    {
        var result = await QuerySingleEventAsync(
            $"DTSTART;TZID=Europe/Zurich:{localStart}\r\nDURATION:PT30M\r\n",
            "2026-03-01T00:00:00Z",
            "2026-11-01T00:00:00Z");

        result.Code.ShouldBe(CalendarEntityQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_DoesNotAcceptWindowsZoneAliasAsIana()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART;TZID=Central Standard Time:20260816T100000\r\nDURATION:PT30M\r\n",
            "2026-08-16T15:00:00Z",
            "2026-08-16T16:00:00Z");

        result.Code.ShouldBe(CalendarEntityQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_InvalidLocalDefinitionNeverFallsBackToSameIanaId()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART;TZID=Europe/Zurich:20260816T100000\r\nDURATION:PT30M\r\n",
            "2026-08-16T08:00:00Z",
            "2026-08-16T09:00:00Z",
            "BEGIN:VTIMEZONE\r\nTZID:Europe/Zurich\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n");

        result.Code.ShouldBe(CalendarEntityQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_ResourceLocalTimeZoneOverridesExternalZoneDatabase()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART;TZID=Custom/Office:20260816T100000\r\nDURATION:PT30M\r\n",
            "2026-08-16T07:00:00Z",
            "2026-08-16T07:01:00Z",
            "BEGIN:VTIMEZONE\r\nTZID:Custom/Office\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0300\r\nTZOFFSETTO:+0300\r\n"
            + "END:STANDARD\r\nEND:VTIMEZONE\r\n");

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task QueryEntitiesAsync_RecurringResourceLocalZoneIsUnevaluableWhenExpansionCannotStayStrict()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART;TZID=Custom/Office:20260815T100000\r\nDURATION:PT30M\r\nRRULE:FREQ=DAILY;COUNT=2\r\n",
            "2026-08-16T08:00:00Z",
            "2026-08-16T08:01:00Z",
            "BEGIN:VTIMEZONE\r\nTZID:Custom/Office\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
            + "END:STANDARD\r\nEND:VTIMEZONE\r\n");

        result.Code.ShouldBe(CalendarEntityQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_RecurringResourceLocalZoneAcrossDstDoesNotCoerce()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART;TZID=Custom/Dst:20260328T100000\r\nDURATION:PT30M\r\nRRULE:FREQ=DAILY;COUNT=3\r\n",
            "2026-03-30T08:00:00Z",
            "2026-03-30T08:01:00Z",
            "BEGIN:VTIMEZONE\r\nTZID:Custom/Dst\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20251026T030000\r\nRRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU\r\n"
            + "TZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nEND:STANDARD\r\n"
            + "BEGIN:DAYLIGHT\r\nDTSTART:20260329T020000\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU\r\n"
            + "TZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n");

        result.Code.ShouldBe(CalendarEntityQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_ResourceLocalRdateTransitionSupportsNonHourOffset()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART;TZID=Custom/Shift:20260816T100000\r\nDURATION:PT30M\r\n",
            "2026-08-16T06:30:00Z",
            "2026-08-16T06:31:00Z",
            "BEGIN:VTIMEZONE\r\nTZID:Custom/Shift\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\nEND:STANDARD\r\n"
            + "BEGIN:DAYLIGHT\r\nDTSTART:20260816T020000\r\nRDATE:20260816T020000\r\n"
            + "TZOFFSETFROM:+0200\r\nTZOFFSETTO:+0330\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n");

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task QueryEntitiesAsync_ResourceLocalOffsetBeyondDateTimeOffsetRangeRemainsEvaluable()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART;TZID=Custom/FarEast:20260816T100000\r\nDURATION:PT30M\r\n",
            "2026-08-15T19:00:00Z",
            "2026-08-15T19:01:00Z",
            "BEGIN:VTIMEZONE\r\nTZID:Custom/FarEast\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+1500\r\nTZOFFSETTO:+1500\r\n"
            + "END:STANDARD\r\nEND:VTIMEZONE\r\n");

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task QueryEntitiesAsync_ConflictingLocalTransitionsAreUnresolved()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART;TZID=Custom/Conflict:20260816T100000\r\nDURATION:PT30M\r\n",
            "2026-08-16T07:00:00Z",
            "2026-08-16T09:00:00Z",
            "BEGIN:VTIMEZONE\r\nTZID:Custom/Conflict\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\nEND:STANDARD\r\n"
            + "BEGIN:DAYLIGHT\r\nDTSTART:20260816T020000\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0300\r\n"
            + "END:DAYLIGHT\r\nEND:VTIMEZONE\r\n");

        result.Code.ShouldBe(CalendarEntityQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_IncompleteLocalObservanceInvalidatesWholeZone()
    {
        var result = await QuerySingleEventAsync(
            "DTSTART;TZID=Custom/Partial:20260816T100000\r\nDURATION:PT30M\r\n",
            "2026-08-16T08:00:00Z",
            "2026-08-16T09:00:00Z",
            "BEGIN:VTIMEZONE\r\nTZID:Custom/Partial\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\nEND:STANDARD\r\n"
            + "BEGIN:DAYLIGHT\r\nDTSTART:20260816T020000\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n");

        result.Code.ShouldBe(CalendarEntityQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("DTSTART:20260329T020000\r\nRRULE:FREQ=YEARLY;COUNT=3;BYMONTH=3;BYDAY=-1SU\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\n", "20260329T023000")]
    [InlineData("DTSTART:20261025T030000\r\nRRULE:FREQ=YEARLY;UNTIL=20281025T030000Z;BYMONTH=10;BYDAY=-1SU\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\n", "20261025T023000")]
    public async Task QueryEntitiesAsync_LocalTimeZoneGapOrOverlapIsUnresolved(
        string observance,
        string localStart)
    {
        var result = await QuerySingleEventAsync(
            $"DTSTART;TZID=Custom/Dst:{localStart}\r\nDURATION:PT30M\r\n",
            "2026-03-01T00:00:00Z",
            "2026-11-01T00:00:00Z",
            $"BEGIN:VTIMEZONE\r\nTZID:Custom/Dst\r\nBEGIN:DAYLIGHT\r\n{observance}END:DAYLIGHT\r\nEND:VTIMEZONE\r\n");

        result.Code.ShouldBe(CalendarEntityQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_DuplicateResourceLocalTimeZoneIdIsUnresolved()
    {
        const string zone = "BEGIN:VTIMEZONE\r\nTZID:Custom/Duplicate\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
            + "END:STANDARD\r\nEND:VTIMEZONE\r\n";
        var result = await QuerySingleEventAsync(
            "DTSTART;TZID=Custom/Duplicate:20260816T100000\r\nDURATION:PT30M\r\n",
            "2026-08-16T08:00:00Z",
            "2026-08-16T08:01:00Z",
            zone + zone);

        result.Code.ShouldBe(CalendarEntityQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_ReportCandidateThatDisappearsIsDiagnosedWithoutInventingSnapshot()
    {
        const string calendarHref = "https://cal.example/events/";
        var vanished = $"{calendarHref}vanished.ics";
        var current = $"{calendarHref}current.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([vanished, current]);
        client.GetCalendarResourceAsync(vanished, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarResourceAsync(current, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(current, "\"new-revision\"", Event("current", "20260816T100000Z", "Current")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Items.ShouldHaveSingleItem().EntityTag.ShouldBe("\"new-revision\"");
        result.Diagnostics.Select(item => item.Code).ShouldContain("resource_disappeared_during_query");
    }

    [Fact]
    public async Task QueryEntitiesAsync_PreservesDistinctAuthorizedCandidateIdentitiesAcrossRedirectAliases()
    {
        const string calendarHref = "https://cal.example/events/";
        var first = $"{calendarHref}old-a.ics";
        var second = $"{calendarHref}old-b.ics";
        var bytes = Event("current", "20260816T100000Z", "Current");
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([first, second]);
        client.GetCalendarResourceAsync(first, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(first, "\"r1\"", bytes));
        client.GetCalendarResourceAsync(second, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(second, "\"r1\"", bytes));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Items.Select(item => item.ResourceHref).ShouldBe([first, second]);
        await client.Received(1).GetCalendarResourceAsync(first, Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(second, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryRedirectedSnapshot_UsesAuthorizedCandidateIdentityForCoherentDirectReread()
    {
        const string calendarHref = "https://cal.example/events/";
        const string candidateHref = "https://cal.example/events/standup.ics";
        const string redirectHref = "https://cal.example/redirected/current.ics";
        var content = Event("standup", "20260816T100000Z", "Standup");
        var requests = new List<Uri>();
        var handler = new RedirectHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsoluteUri == candidateHref)
            {
                return new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
                {
                    Headers = { Location = new Uri(redirectHref) }
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"revision-1\"") },
                Content = new ByteArrayContent(content)
            };
        });
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = calendarHref,
            DefaultEventCalendarName = "Events"
        });
        using var httpClient = new HttpClient(handler);
        var transport = new CalDavClient(httpClient, options, Substitute.For<ILogger<CalDavClient>>());
        var client = new RedirectQueryCalendarClient(
            transport,
            EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised),
            candidateHref);
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());

        var query = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            CancellationToken.None);
        var snapshot = query.Items.ShouldHaveSingleItem();
        var reread = await sut.GetResourceAsync(snapshot.ResourceHref, CancellationToken.None);

        snapshot.ResourceHref.ShouldBe(candidateHref);
        snapshot.EntityTag.ShouldBe("\"revision-1\"");
        reread.Code.ShouldBe(CalendarResourceReadCode.Success);
        reread.Snapshot!.ResourceHref.ShouldBe(candidateHref);
        reread.Snapshot.EntityTag.ShouldBe(snapshot.EntityTag);
        reread.Snapshot.AuthoritativeUtf8.ToArray().ShouldBe(snapshot.AuthoritativeUtf8.ToArray());
        requests.Select(uri => uri.AbsoluteUri).ShouldBe(
            [candidateHref, redirectHref, candidateHref, redirectHref]);
    }

    [Fact]
    public async Task QueryReportRedirectOutsideCalendarIdentity_FailsClosedAndCannotBeDirectlyReread()
    {
        const string calendarHref = "https://cal.example/events/";
        const string redirectedCalendarHref = "https://cal.example/redirected/";
        const string redirectedResourceHref = "https://cal.example/redirected/a.ics";
        var requests = new List<HttpRequestMessage>();
        var handler = new RedirectHttpMessageHandler(request =>
        {
            requests.Add(request);
            if (request.RequestUri!.AbsoluteUri == calendarHref)
            {
                return new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
                {
                    Headers = { Location = new Uri(redirectedCalendarHref) }
                };
            }
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>a.ics</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response></d:multistatus>")
            };
        });
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = calendarHref,
            DefaultEventCalendarName = "Events"
        });
        using var httpClient = new HttpClient(handler);
        var transport = new CalDavClient(httpClient, options, Substitute.For<ILogger<CalDavClient>>());
        var client = new DelegatingQueryCalendarClient(
            transport,
            EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised));
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            CancellationToken.None));
        var directRead = await sut.GetResourceAsync(redirectedResourceHref, CancellationToken.None);

        directRead.Code.ShouldBe(CalendarResourceReadCode.OutsideScope);
        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(request => request.Method.Method == "REPORT");
    }

    [Fact]
    public async Task QueryEntitiesAsync_WeakCandidateRevisionFailsWholeQueryWithoutPartialItems()
    {
        const string calendarHref = "https://cal.example/events/";
        var current = $"{calendarHref}current.ics";
        var weak = $"{calendarHref}weak.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([current, weak]);
        client.GetCalendarResourceAsync(current, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(current, "\"r1\"", Event("current", "20260816T100000Z", "Current")));
        client.GetCalendarResourceAsync(weak, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.ConcurrencyUnavailable));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.ConcurrencyUnavailable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryEntitiesAsync_OversizedCandidateCarriesTruthfulObservedByteCount()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/large.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, null, null, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.PayloadTooLarge, ObservedByteCount: 4_194_305));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarEntityQueryCode.PayloadTooLarge);
        result.Items.ShouldBeEmpty();
        result.Limits.ShouldBe(new CalendarEntityQueryExecutionLimits(ByteCount: 4_194_305));
    }

    [Fact]
    public async Task QueryEntitiesAsync_StopsBeforeDirectGetWhenDeadlineExpiresAfterReport()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/a.ics";
        using var cancellation = new CancellationTokenSource();
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromResult<IReadOnlyList<string>>([resourceHref]);
            });

        await Should.ThrowAsync<OperationCanceledException>(() => sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event]),
            cancellation.Token));

        await client.DidNotReceive().GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryEntitiesAsync_CallerCancellationBeforeTemporalExpansionPropagatesWithNoItems()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/recurring.ics";
        var enteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        var from = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-15T13:00:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, from, to, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            enteredRead.TrySetResult();
            await releaseRead.Task;
            return CalendarResourceRead.Success(
                resourceHref,
                "\"r1\"",
                RecurringEventWithRule("recurring", "RRULE:FREQ=SECONDLY;COUNT=2000\r\n"));
        });

        var pending = sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event], from, to),
            cancellation.Token);
        await enteredRead.Task;
        cancellation.Cancel();
        releaseRead.TrySetResult();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData("https://other.example/events/", CalendarEntityQueryCode.UnsafeScope)]
    [InlineData("https://user:secret@cal.example/events/", CalendarEntityQueryCode.UnsafeScope)]
    [InlineData("https://cal.example/events/#fragment", CalendarEntityQueryCode.UnsafeScope)]
    [InlineData("https://cal.example/events/?private=true", CalendarEntityQueryCode.UnsafeScope)]
    [InlineData("https://cal.example/events%2Fprivate/", CalendarEntityQueryCode.UnsafeScope)]
    [InlineData("https://cal.example/events/%2e%2e/private/", CalendarEntityQueryCode.UnsafeScope)]
    [InlineData("https://cal.example/events\\private/", CalendarEntityQueryCode.UnsafeScope)]
    [InlineData("https://cal.example/private/", CalendarEntityQueryCode.OutsideScope)]
    public async Task QueryEntitiesAsync_PrevalidatesSelectedHrefBeforeDiscovery(
        string href,
        CalendarEntityQueryCode expectedCode)
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                CalendarHrefs = "https://cal.example/events/"
            }),
            Substitute.For<ILogger<CalendarService>>());

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: href)),
                [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryEntitiesAsync_FiltersActualKindBeforeTemporalDiagnostics()
    {
        const string calendarHref = "https://cal.example/mixed/";
        const string resourceHref = "https://cal.example/mixed/todo.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Mixed" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Mixed", EntityKindSupport.Advertised, EntityKindSupport.Advertised)]);
        var from = DateTimeOffset.Parse("2026-08-16T09:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-16T11:00:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, from, to, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", TodoWithTiming("todo", "20260816T100000", "20260816T103000")));

        var result = await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event], from, to),
            CancellationToken.None);

        result.Items.ShouldBeEmpty();
        result.Diagnostics.Select(item => item.Code).ShouldNotContain("temporal_filter_unresolved");
    }

    [Fact]
    public async Task GetResourceAsync_ReturnsOneRevisionCoherentEventSnapshot()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/standup.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nSUMMARY:Standup\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = calendarHref
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [Calendar(calendarHref, "Events", EntityKindSupport.NotAdvertised)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"revision-1\"", System.Text.Encoding.UTF8.GetBytes(content)));

        var result = await sut.GetResourceAsync(resourceHref, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        result.Snapshot!.CalendarHref.ShouldBe(calendarHref);
        result.Snapshot.ResourceHref.ShouldBe(resourceHref);
        result.Snapshot.EntityTag.ShouldBe("\"revision-1\"");
        result.Snapshot.AuthoritativeUtf8.ToArray().ShouldBe(System.Text.Encoding.UTF8.GetBytes(content));
        result.Snapshot.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Event);
        result.Snapshot.Projection.EntityUid.ShouldBe("event-1");
        result.Snapshot.Projection.Summary.ShouldBe("Standup");
    }

    [Fact]
    public async Task GetResourceAsync_ReturnsMixedEntityKindsAsOpaqueWithSafeDiagnostic()
    {
        const string calendarHref = "https://cal.example/mixed/";
        const string resourceHref = "https://cal.example/mixed/mixed.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260815T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", CalendarHrefs = calendarHref });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [Calendar(calendarHref, "Mixed", EntityKindSupport.Advertised)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"revision-1\"", System.Text.Encoding.UTF8.GetBytes(content)));

        var result = await sut.GetResourceAsync(resourceHref, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        result.Snapshot!.Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        result.Snapshot.Projection.EntityUid.ShouldBeNull();
        result.Snapshot.SemanticMutationAvailable.ShouldBeFalse();
        result.Snapshot.Diagnostics.Select(item => item.Code).ShouldBe(["mixed_entity_kinds"]);
        JsonSerializer.Serialize(result.Snapshot.Diagnostics).ShouldNotContain("event-1");
        JsonSerializer.Serialize(result.Snapshot.Diagnostics).ShouldNotContain("todo-1");
    }

    [Fact]
    public async Task GetResourceAsync_TreatsContentUrisAsInertData()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/inert.ics";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nURL:https://attacker.invalid/url\r\nATTACH;VALUE=URI:https://attacker.invalid/attachment\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", CalendarHrefs = calendarHref }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [Calendar(calendarHref, "Events", EntityKindSupport.NotAdvertised)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"revision-1\"", System.Text.Encoding.UTF8.GetBytes(content)));

        var result = await sut.GetResourceAsync(resourceHref, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        result.Snapshot!.CalendarProperties.Single(property => property.Name == "ATTACH").RawEncodedValue
            .ShouldBe("https://attacker.invalid/attachment");
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.Received(1).GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://other.example/events/a.ics", CalendarResourceReadCode.InvalidInput)]
    [InlineData("https://user:secret@cal.example/events/a.ics", CalendarResourceReadCode.InvalidInput)]
    [InlineData("https://cal.example/events/a.ics#fragment", CalendarResourceReadCode.InvalidInput)]
    [InlineData("/events/a.ics", CalendarResourceReadCode.InvalidInput)]
    [InlineData("https://cal.example/events%2Fprivate/a.ics", CalendarResourceReadCode.InvalidInput)]
    [InlineData("https://cal.example/events%5cprivate/a.ics", CalendarResourceReadCode.InvalidInput)]
    [InlineData("https://cal.example/private/a.ics", CalendarResourceReadCode.OutsideScope)]
    public async Task GetResourceAsync_RejectsInvalidOrOutOfScopeHrefBeforeNetwork(
        string href,
        CalendarResourceReadCode expectedCode)
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/events/"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());

        var result = await sut.GetResourceAsync(href, CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveDefaultCalendarAsync_UsesIndependentEventAndTodoDefaults()
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            DefaultEventCalendarName = "Events",
            DefaultTodoCalendarName = "To-dos"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new CalendarDescriptor
            {
                Href = "https://cal.example/events/",
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            },
            new CalendarDescriptor
            {
                Href = "https://cal.example/todos/",
                DisplayName = "To-dos",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.NotAdvertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);

        var eventResult = await sut.ResolveDefaultCalendarAsync(CalendarEntityKind.Event, CancellationToken.None);
        var todoResult = await sut.ResolveDefaultCalendarAsync(CalendarEntityKind.Todo, CancellationToken.None);

        eventResult.Calendar!.Href.ShouldBe("https://cal.example/events/");
        todoResult.Calendar!.Href.ShouldBe("https://cal.example/todos/");
    }

    [Fact]
    public async Task ResolveDefaultCalendarAsync_ResolvesAuthorizedMatchBeyondCandidateLimit()
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            DefaultTodoCalendarName = "Target"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        var calendars = Enumerable.Range(0, 33)
            .Select(index => Calendar($"https://cal.example/{index:D2}/", $"Calendar {index:D2}", EntityKindSupport.Advertised))
            .Append(Calendar("https://cal.example/target/", "Target", EntityKindSupport.Advertised))
            .ToArray();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(calendars);

        var result = await sut.ResolveDefaultCalendarAsync(CalendarEntityKind.Todo, CancellationToken.None);

        result.Code.ShouldBe(CalendarSelectionCode.Success);
        result.Calendar!.Href.ShouldBe("https://cal.example/target/");
    }

    [Theory]
    [InlineData("Work", CalendarSelectionCode.Success)]
    [InlineData("Unknown", CalendarSelectionCode.Success)]
    [InlineData("Missing", CalendarSelectionCode.NotFound)]
    [InlineData("Duplicate", CalendarSelectionCode.Ambiguous)]
    [InlineData("Private", CalendarSelectionCode.OutsideScope)]
    [InlineData("Events", CalendarSelectionCode.UnsupportedCapability)]
    public async Task ResolveDefaultCalendarAsync_ReturnsDeterministicTypedOutcome(
        string configuredName,
        CalendarSelectionCode expectedCode)
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/work/,https://cal.example/unknown/,https://cal.example/duplicate-a/,https://cal.example/duplicate-b/,https://cal.example/events/",
            DefaultTodoCalendarName = configuredName
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Calendar("https://cal.example/work/", "Work", EntityKindSupport.Advertised),
            Calendar("https://cal.example/unknown/", "Unknown", EntityKindSupport.Unknown),
            Calendar("https://cal.example/duplicate-a/", "Duplicate", EntityKindSupport.Advertised),
            Calendar("https://cal.example/duplicate-b/", " duplicate ", EntityKindSupport.Advertised),
            Calendar("https://cal.example/private/", "Private", EntityKindSupport.Advertised),
            Calendar("https://cal.example/events/", "Events", EntityKindSupport.NotAdvertised)
        ]);

        var result = await sut.ResolveDefaultCalendarAsync(CalendarEntityKind.Todo, CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        if (expectedCode == CalendarSelectionCode.Success && configuredName == "Work")
            result.Calendar!.Href.ShouldBe("https://cal.example/work/");
        if (expectedCode == CalendarSelectionCode.Ambiguous)
            result.Candidates.Select(candidate => candidate.Href).ShouldBe(
                ["https://cal.example/duplicate-a/", "https://cal.example/duplicate-b/"]);
        if (expectedCode != CalendarSelectionCode.Success)
        {
            result.Candidates.ShouldNotBeEmpty();
            result.Candidates.ShouldAllBe(candidate => candidate.Href != "https://cal.example/private/");
            result.Candidates.All(candidate => candidate.DisplayName is not null).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task GetCalendarsAsync_AppliesExactScopeAndPreservesDiscoveryEvidence()
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/a/,https://cal.example/missing/,https://cal.example/a/,https://cal.example/missing/"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new CalendarDescriptor
            {
                Href = "https://cal.example/b/",
                DisplayName = "Work",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Unknown,
                TodoSupport = EntityKindSupport.NotAdvertised,
                TodoEvidence = [new CapabilityEvidence("supported-calendar-component-set", "VEVENT")]
            },
            new CalendarDescriptor
            {
                Href = "https://cal.example/a/",
                DisplayName = "a",
                DisplayNameProvenance = DisplayNameProvenance.DerivedFromHref,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.Advertised,
                EventEvidence = [new CapabilityEvidence("supported-calendar-component-set", "VEVENT,VTODO")],
                TodoEvidence = [new CapabilityEvidence("supported-calendar-component-set", "VEVENT,VTODO")]
            }
        ]);

        var result = await sut.GetCalendarsAsync(CancellationToken.None);

        result.Items.Select(calendar => calendar.Href).ShouldBe(["https://cal.example/a/"]);
        result.Items[0].DisplayNameProvenance.ShouldBe(DisplayNameProvenance.DerivedFromHref);
        result.Items[0].EventSupport.ShouldBe(EntityKindSupport.Advertised);
        result.Items[0].TodoSupport.ShouldBe(EntityKindSupport.Advertised);
        result.Diagnostics.Select(diagnostic => diagnostic.Code)
            .ShouldBe(["duplicate_calendar_href", "duplicate_calendar_href", "calendar_href_not_found"]);
    }

    [Fact]
    public async Task GetCalendarsAsync_WithoutConfiguredScope_ReturnsAllUniqueCalendarsInCanonicalOrder()
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Calendar("https://cal.example/z/", "Z", EntityKindSupport.Advertised),
            Calendar("https://cal.example/a/", "A", EntityKindSupport.Advertised),
            Calendar("https://cal.example/a/", "Duplicate A", EntityKindSupport.Advertised)
        ]);

        var result = await sut.GetCalendarsAsync(CancellationToken.None);

        result.Items.Select(calendar => calendar.Href).ShouldBe(
            ["https://cal.example/a/", "https://cal.example/z/"]);
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetCalendarsAsync_DiagnosticsDoNotExposeConfiguredHrefText()
    {
        const string unsafeHref = "https://user:secret@cal.example/private/";
        var oversizedHref = $"https://cal.example/{new string('x', 10_000)}";
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = $"{unsafeHref},{oversizedHref}"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var result = await sut.GetCalendarsAsync(CancellationToken.None);

        var serializedDiagnostics = JsonSerializer.Serialize(result.Diagnostics);
        serializedDiagnostics.ShouldNotContain(unsafeHref);
        serializedDiagnostics.ShouldNotContain(oversizedHref);
        result.Diagnostics.Select(diagnostic => diagnostic.Code)
            .ShouldBe(["calendar_href_not_found", "calendar_href_not_found"]);
    }

    [Fact]
    public async Task GetCalendarsAsync_DeduplicatesAndBoundsDiscoveredCalendars()
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/000/"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        var calendars = Enumerable.Range(0, 257)
            .Select(index => Calendar($"https://cal.example/{index:D3}/", $"Calendar {index:D3}", EntityKindSupport.Advertised))
            .Append(Calendar("https://cal.example/000/", "Duplicate", EntityKindSupport.Advertised))
            .ToArray();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(calendars);

        var exception = await Should.ThrowAsync<CalendarDiscoveryLimitException>(() =>
            sut.GetCalendarsAsync(CancellationToken.None));

        exception.CalendarCount.ShouldBe(257);
    }

    private static CalendarDescriptor Calendar(string href, string displayName, EntityKindSupport todoSupport) =>
        new()
        {
            Href = href,
            DisplayName = displayName,
            DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
            EventSupport = EntityKindSupport.Advertised,
            TodoSupport = todoSupport
        };

    private static CalendarDescriptor EntityCalendar(
        string href,
        string displayName,
        EntityKindSupport eventSupport,
        EntityKindSupport todoSupport) => new()
        {
            Href = href,
            DisplayName = displayName,
            DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
            EventSupport = eventSupport,
            TodoSupport = todoSupport
        };

    private static byte[] Event(string uid, string start, string summary) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:{start}\r\nSUMMARY:{summary}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] EventWithEnd(string uid, string start, string end) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:{start}\r\nDTEND:{end}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] Todo(string uid, string summary) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nSUMMARY:{summary}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

    private static byte[] TodoWithTiming(string uid, string? start, string due)
    {
        var startLine = start is null ? string.Empty : $"DTSTART:{start}\r\n";
        return System.Text.Encoding.UTF8.GetBytes(
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\n{startLine}DUE:{due}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
    }

    private static byte[] EventWithRawStart(string uid, string parameter, string start) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART{parameter}:{start}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] RecurringEvent(string uid, string? exceptionDate)
    {
        var exceptionLine = exceptionDate is null ? string.Empty : $"EXDATE:{exceptionDate}\r\n";
        return System.Text.Encoding.UTF8.GetBytes(
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260814T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=4\r\n{exceptionLine}END:VEVENT\r\nEND:VCALENDAR\r\n");
    }

    private static byte[] RecurringTodo(string uid) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260815T100000Z\r\nDUE:20260815T103000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

    private static byte[] RecurringEventWithRule(string uid, string recurrenceLines) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260815T120000Z\r\nDURATION:PT1S\r\n{recurrenceLines}END:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] Mixed(string eventUid, string todoUid) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{eventUid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nBEGIN:VTODO\r\nUID:{todoUid}\r\nDTSTAMP:20260815T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

    private static async Task<CalendarEntityQueryResult> QuerySingleEventAsync(
        string temporalLines,
        string from,
        string to,
        string calendarLines = "")
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/temporal.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        var start = DateTimeOffset.Parse(from);
        var end = DateTimeOffset.Parse(to);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, start, end, Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n{calendarLines}"
            + $"BEGIN:VEVENT\r\nUID:temporal\r\nDTSTAMP:20260815T120000Z\r\n{temporalLines}END:VEVENT\r\nEND:VCALENDAR\r\n");
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>())
            .Returns(CalendarResourceRead.Success(resourceHref, "\"r1\"", bytes));
        return await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event], start, end),
            CancellationToken.None);
    }

    private static async Task<CalendarEntityQueryResult> QueryRecurringCountsAsync(params int[] occurrenceCounts)
    {
        const string calendarHref = "https://cal.example/events/";
        var hrefs = occurrenceCounts.Select((_, index) => $"{calendarHref}{index}.ics").ToArray();
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example", DefaultEventCalendarName = "Events" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        var from = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-15T13:00:00Z");
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event, from, to, Arg.Any<CancellationToken>())
            .Returns(hrefs);
        for (var index = 0; index < hrefs.Length; index++)
        {
            var resourceIndex = index;
            client.GetCalendarResourceAsync(hrefs[index], Arg.Any<CancellationToken>()).Returns(
                CalendarResourceRead.Success(
                    hrefs[index],
                    $"\"r{index}\"",
                    RecurringEventWithRule(
                        $"recurring-{index}",
                        $"RRULE:FREQ=SECONDLY;COUNT={occurrenceCounts[resourceIndex]}\r\n")));
        }
        return await sut.QueryEntitiesAsync(
            new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event], from, to),
            CancellationToken.None);
    }

    private sealed class RedirectQueryCalendarClient(
        ICalendarClient transport,
        CalendarDescriptor calendar,
        string candidateHref) : ICalendarClient
    {
        public Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CalendarDescriptor>>([calendar]);

        public Task<IReadOnlyList<string>> QueryCalendarResourceHrefsAsync(
            string calendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([candidateHref]);

        public Task<CalendarResourceRead> GetCalendarResourceAsync(string href, CancellationToken cancellationToken) =>
            transport.GetCalendarResourceAsync(href, cancellationToken);
    }

    private sealed class DelegatingQueryCalendarClient(
        ICalendarClient transport,
        CalendarDescriptor calendar) : ICalendarClient
    {
        public Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CalendarDescriptor>>([calendar]);

        public Task<IReadOnlyList<string>> QueryCalendarResourceHrefsAsync(
            string calendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken) => transport.QueryCalendarResourceHrefsAsync(
                calendarHref,
                entityKind,
                from,
                to,
                cancellationToken);

        public Task<CalendarResourceRead> GetCalendarResourceAsync(string href, CancellationToken cancellationToken) =>
            transport.GetCalendarResourceAsync(href, cancellationToken);
    }

    private sealed class RedirectHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
