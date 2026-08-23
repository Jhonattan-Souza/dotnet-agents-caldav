using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
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
    public async Task GetCalendarsAsync_ConcurrentConsumersShareOneCompleteAcquisition()
    {
        var client = Substitute.For<ICalendarClient>();
        var acquisition = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(acquisition.Task);
        var sut = Service(client);

        var first = sut.GetCalendarsAsync(CancellationToken.None);
        var second = sut.GetCalendarsAsync(CancellationToken.None);
        acquisition.SetResult([EntityCalendar(
            "https://cal.example/events/",
            "Events",
            EntityKindSupport.Advertised,
            EntityKindSupport.NotAdvertised)]);

        (await first).Items.ShouldHaveSingleItem();
        (await second).Items.ShouldHaveSingleItem();
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCalendarsAsync_SharedFailureIsMemoizedForTheToolCall()
    {
        var client = Substitute.For<ICalendarClient>();
        var failure = new CalendarDiscoveryProtocolException("private upstream response");
        client.GetCalendarsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<CalendarDescriptor>>(failure));
        var sut = Service(client);

        var first = await Should.ThrowAsync<CalendarDiscoveryProtocolException>(
            () => sut.GetCalendarsAsync(CancellationToken.None));
        var second = await Should.ThrowAsync<CalendarDiscoveryProtocolException>(
            () => sut.ResolveDefaultCalendarAsync(CalendarEntityKind.Event, CancellationToken.None));

        first.ShouldBeSameAs(failure);
        second.ShouldBeSameAs(failure);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCalendarsAsync_SharedCancellationStopsTheSingleAcquisition()
    {
        var client = Substitute.For<ICalendarClient>();
        var acquisition = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            var token = call.Arg<CancellationToken>();
            token.Register(() => acquisition.TrySetCanceled(token));
            return acquisition.Task;
        });
        var sut = Service(client);
        using var cancellation = new CancellationTokenSource();

        var first = sut.GetCalendarsAsync(cancellation.Token);
        var second = sut.GetCalendarsAsync(cancellation.Token);
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => first);
        await Should.ThrowAsync<OperationCanceledException>(() => second);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCalendarsAsync_OneCancelledConsumerDoesNotCancelSharedOperationWork()
    {
        var client = Substitute.For<ICalendarClient>();
        var acquisition = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(acquisition.Task);
        var sut = Service(client);
        using var independentConsumer = new CancellationTokenSource();

        var cancelled = sut.GetCalendarsAsync(independentConsumer.Token);
        var operation = sut.GetCalendarsAsync(CancellationToken.None);
        independentConsumer.Cancel();
        acquisition.SetResult([EntityCalendar(
            "https://cal.example/events/",
            "Events",
            EntityKindSupport.Advertised,
            EntityKindSupport.NotAdvertised)]);

        await Should.ThrowAsync<OperationCanceledException>(() => cancelled);
        (await operation).Items.ShouldHaveSingleItem();
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCalendarsAsync_CancelledToolCallDoesNotPoisonAnotherToolCall()
    {
        var client = Substitute.For<ICalendarClient>();
        var firstAcquisition = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            var token = call.Arg<CancellationToken>();
            if (Interlocked.Increment(ref calls) == 1)
            {
                token.Register(() => firstAcquisition.TrySetCanceled(token));
                return firstAcquisition.Task;
            }
            return Task.FromResult<IReadOnlyList<CalendarDescriptor>>([EntityCalendar(
                "https://cal.example/events/",
                "Events",
                EntityKindSupport.Advertised,
                EntityKindSupport.NotAdvertised)]);
        });
        using var cancelled = new CancellationTokenSource();

        var cancelledCall = Service(client).GetCalendarsAsync(cancelled.Token);
        cancelled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => cancelledCall);
        var unrelated = await Service(client).GetCalendarsAsync(CancellationToken.None);

        unrelated.Items.ShouldHaveSingleItem();
        await client.Received(2).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCalendarsAsync_NewToolCallDoesNotReuseReviewTimeDiscovery()
    {
        var client = Substitute.For<ICalendarClient>();
        var calls = 0;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<CalendarDescriptor>>([EntityCalendar(
                $"https://cal.example/events-{calls}/",
                $"Events {calls}",
                EntityKindSupport.Advertised,
                EntityKindSupport.NotAdvertised)]);
        });
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "principal",
            CalendarHrefs = "https://cal.example/events-1/,https://cal.example/events-2/"
        });

        var reviewCall = Service(client, options);
        var executionCall = Service(client, options);
        var reviewed = await reviewCall.GetCalendarsAsync(CancellationToken.None);
        var executed = await executionCall.GetCalendarsAsync(CancellationToken.None);

        reviewed.Items.ShouldHaveSingleItem().Href.ShouldBe("https://cal.example/events-1/");
        executed.Items.ShouldHaveSingleItem().Href.ShouldBe("https://cal.example/events-2/");
        await client.Received(2).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCalendarsAsync_ContextChangesUseIsolatedAcquisitions()
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var context = new CalDavOptions
        {
            BaseUrl = "https://cal.example/server/",
            Username = "principal-a",
            Password = "secret-a",
            CalendarHrefs = "https://cal.example/a/",
            DefaultEventCalendarName = "Events"
        };
        var sut = Service(client, Options.Create(context));

        await sut.GetCalendarsAsync(CancellationToken.None);
        context.Password = string.Concat("secret", "-b");
        await sut.GetCalendarsAsync(CancellationToken.None);
        context.Username = "principal-b";
        await sut.GetCalendarsAsync(CancellationToken.None);
        context.BaseUrl = "https://other.example/server/";
        await sut.GetCalendarsAsync(CancellationToken.None);
        context.CalendarHrefs = "https://other.example/b/";
        await sut.GetCalendarsAsync(CancellationToken.None);
        context.DefaultEventCalendarName = "Archive";
        await sut.GetCalendarsAsync(CancellationToken.None);

        await client.Received(6).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCalendarsAsync_RetainsOnlyCompleteInScopeDescriptors()
    {
        const string inScope = "https://cal.example/events/";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            EntityCalendar(inScope, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised),
            EntityCalendar(
                "https://cal.example/private/",
                "Private",
                EntityKindSupport.Advertised,
                EntityKindSupport.Advertised)
        ]);
        var sut = Service(client, Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "principal",
            Password = "private-password",
            CalendarHrefs = inScope
        }));

        var first = await sut.GetCalendarsAsync(CancellationToken.None);
        var second = await sut.GetCalendarsAsync(CancellationToken.None);

        first.Items.ShouldHaveSingleItem().Href.ShouldBe(inScope);
        second.Items.ShouldHaveSingleItem().Href.ShouldBe(inScope);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCalendarsAsync_FreezesDiscoveryEvidenceForTheToolCall()
    {
        var evidence = new List<CapabilityEvidence> { new("supported-calendar-component-set", "VEVENT") };
        var calendars = new List<CalendarDescriptor>
        {
            EntityCalendar(
                "https://cal.example/events/",
                "Events",
                EntityKindSupport.Advertised,
                EntityKindSupport.NotAdvertised) with
            {
                EventEvidence = evidence
            }
        };
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(calendars);
        var sut = Service(client);

        var first = await sut.GetCalendarsAsync(CancellationToken.None);
        calendars.Clear();
        evidence.Clear();
        var second = await sut.GetCalendarsAsync(CancellationToken.None);

        first.Items.ShouldHaveSingleItem().EventEvidence.ShouldHaveSingleItem();
        second.Items.ShouldHaveSingleItem().EventEvidence.ShouldHaveSingleItem();
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, CalendarOccurrenceQueryCode.InvalidInput)]
    [InlineData(366, CalendarOccurrenceQueryCode.Success)]
    [InlineData(367, CalendarOccurrenceQueryCode.InvalidInput)]
    public async Task QueryOccurrencesAsync_EnforcesExactTemporalWindowBoundary(
        int days,
        CalendarOccurrenceQueryCode expectedCode)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(CalendarEntityScope.All, from, from.AddDays(days)),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_ExpandsBoundedUtcEventRecurrenceLocally()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/recurring.ics";
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
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", RecurringEvent("recurring", null)));

        var phases = new List<CalendarOperationPhase>();
        var progressState = CalendarOperationProgress.CreateState(phases.Add);
        CalendarOccurrenceQueryResult result;
        using (CalendarOperationProgress.Attach(progressState))
        {
            result = await sut.QueryOccurrencesAsync(
                new CalendarOccurrenceQuery(
                    CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                    DateTimeOffset.Parse("2026-08-15T00:00:00Z"),
                    DateTimeOffset.Parse("2026-08-17T00:00:00Z")),
                CancellationToken.None);
        }

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        phases.ShouldBe([CalendarOperationPhase.Filter, CalendarOperationPhase.Expand]);
        result.Items.Select(item => item.RecurrenceIdentity.Value)
            .ShouldBe(["2026-08-15T10:00:00Z", "2026-08-16T10:00:00Z"]);
        result.Items.Select(item => item.Timing.EffectiveStart.Value)
            .ShouldBe(["2026-08-15T10:00:00Z", "2026-08-16T10:00:00Z"]);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_PreservesRequestedWindowInCandidatePlanning()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/recurring.ics";
        var from = DateTimeOffset.Parse("2026-08-17T20:30:00Z");
        var to = DateTimeOffset.Parse("2026-08-17T20:45:00Z");
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                from,
                to,
                Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                from,
                to,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", OverrideEvent()));

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                from,
                to),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2026-08-17T10:00:00Z");
        await client.Received(1).QueryCalendarResourceHrefsAsync(
            calendarHref,
            CalendarEntityKind.Event,
            from,
            to,
            Arg.Any<CancellationToken>());
        await client.Received(1).QueryCalendarResourceHrefsAsync(
            calendarHref,
            CalendarEntityKind.Todo,
            from,
            to,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryOccurrencesAsync_FetchesFiveCandidatesInOneAuthoritativeBatch()
    {
        const string calendarHref = "https://cal.example/events/";
        var from = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-16T11:00:00Z");
        var hrefs = Enumerable.Range(1, 5).Select(index => $"{calendarHref}{index}.ics").ToArray();
        var requests = new List<(HttpMethod Method, string Body)>();
        var handler = new AsyncHttpMessageHandler(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            requests.Add((request.Method, body));
            _ = await Delayed(true);
            var responseBody = body.Contains("calendar-multiget", StringComparison.Ordinal)
                ? MultigetResponse(hrefs)
                : body.Contains("name=\"VEVENT\"", StringComparison.Ordinal)
                    ? CandidateResponse(hrefs)
                    : CandidateResponse([]);
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(responseBody)
            };
        });
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = calendarHref
        });
        using var httpClient = new HttpClient(handler);
        var transport = new CalDavClient(httpClient, options, Substitute.For<ILogger<CalDavClient>>());
        var client = new DelegatingQueryCalendarClient(
            transport,
            EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised));
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());

        var stopwatch = Stopwatch.StartNew();
        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                from,
                to),
            CancellationToken.None);
        stopwatch.Stop();

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.Count.ShouldBe(5);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
        requests.Count.ShouldBe(3);
        requests.ShouldAllBe(request => request.Method.Method == "REPORT");
        requests.Count(request => request.Body.Contains("calendar-query", StringComparison.Ordinal)).ShouldBe(2);
        requests.Count(request => request.Body.Contains("calendar-multiget", StringComparison.Ordinal)).ShouldBe(1);
        hrefs.ShouldAllBe(href => requests[2].Body.Contains(href, StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryOccurrencesAsync_RequestCountGrowsByBoundedBatches()
    {
        const string calendarHref = "https://cal.example/events/";
        var from = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-16T11:00:00Z");
        var hrefs = Enumerable.Range(1, 51).Select(index => $"{calendarHref}{index}.ics").ToArray();
        var batchSizes = new List<int>();
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                from,
                to,
                Arg.Any<CancellationToken>())
            .Returns(hrefs);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                from,
                to,
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCalendarResourcesForQueryAsync(
                calendarHref,
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var batch = call.ArgAt<IReadOnlyList<string>>(1);
                batchSizes.Add(batch.Count);
                return batch.Select((href, index) => CalendarResourceRead.Success(
                    href,
                    $"\"r{index + 1}\"",
                    Event(href, "20260816T103000Z", href))).ToArray();
            });

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                from,
                to),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.Count.ShouldBe(51);
        batchSizes.ShouldBe([50, 1]);
        await client.Received(2).GetCalendarResourcesForQueryAsync(
            calendarHref,
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryOccurrencesAsync_SelectedScopeIncludesEventsAndEveryTimedTodoForm()
    {
        const string calendarHref = "https://cal.example/mixed/";
        var hrefs = new[] { "event", "todo-span", "todo-due", "todo-start", "todo-none" }
            .ToDictionary(name => name, name => $"{calendarHref}{name}.ics");
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Mixed", EntityKindSupport.Advertised, EntityKindSupport.Advertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([hrefs["event"]]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([hrefs["todo-span"], hrefs["todo-due"], hrefs["todo-start"], hrefs["todo-none"]]);
        client.GetCalendarResourceAsync(hrefs["event"], Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(hrefs["event"], "\"e1\"", Event("event", "20260816T101500Z", "Event")));
        client.GetCalendarResourceAsync(hrefs["todo-span"], Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(hrefs["todo-span"], "\"t1\"", TodoWithTemporalLines(
                "todo-span", "DTSTART:20260816T094500Z\r\nDUE:20260816T101500Z\r\n")));
        client.GetCalendarResourceAsync(hrefs["todo-due"], Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(hrefs["todo-due"], "\"t2\"", TodoWithTemporalLines(
                "todo-due", "DUE:20260816T102000Z\r\n")));
        client.GetCalendarResourceAsync(hrefs["todo-start"], Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(hrefs["todo-start"], "\"t3\"", TodoWithTemporalLines(
                "todo-start", "DTSTART:20260816T103000Z\r\n")));
        client.GetCalendarResourceAsync(hrefs["todo-none"], Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(hrefs["todo-none"], "\"t4\"", Todo("todo-none", "No timing")));

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T11:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.Select(item => item.Snapshot.Projection.EntityUid)
            .ShouldBe(["todo-span", "event", "todo-due", "todo-start"]);
        result.Items.ShouldNotContain(item => item.Snapshot.Projection.EntityUid == "todo-none");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_ExdateAndCancellationSuppressWhileMovedTimingRetainsOriginalIdentity()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/overrides.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", OverrideEvent()));

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse("2026-08-17T20:30:00Z"),
                DateTimeOffset.Parse("2026-08-17T20:45:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-17T10:00:00Z");
        occurrence.Timing.SourceStart.Value.ShouldBe("2026-08-17T10:00:00Z");
        occurrence.Timing.SourceEnd!.Value.ShouldBe("2026-08-17T11:00:00Z");
        occurrence.Timing.SourceDuration.ShouldBe("PT1H");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-17T20:00:00Z");
        occurrence.Timing.EffectiveEnd!.Value.ShouldBe("2026-08-17T21:00:00Z");

        var broad = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-18T00:00:00Z")),
            CancellationToken.None);
        broad.Items.Select(item => item.RecurrenceIdentity.Value)
            .ShouldBe(["2026-08-14T10:00:00Z", "2026-08-17T10:00:00Z"]);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_NearestRangeReplacesEarlierAndIndividualOverrideWins()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/ranges.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", RangeOverrideEvent()));

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse("2026-08-16T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-19T00:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.Select(item => (item.RecurrenceIdentity.Value, item.Timing.EffectiveStart.Value, item.Timing.EffectiveEnd!.Value))
            .ShouldBe([
                ("2026-08-16T09:00:00Z", "2026-08-16T11:00:00Z", "2026-08-16T12:00:00Z"),
                ("2026-08-17T09:00:00Z", "2026-08-17T13:00:00Z", "2026-08-17T15:00:00Z"),
                ("2026-08-18T09:00:00Z", "2026-08-18T16:00:00Z", "2026-08-18T17:00:00Z")
            ]);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_MovedRangeMatchesEffectiveWindowAndRetainsOriginalIdentity()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260814T090000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=5\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260817T090000Z\r\n"
            + "DTSTART:20260817T130000Z\r\nDURATION:PT2H\r\n",
            "2026-08-17T13:30:00Z",
            "2026-08-17T13:45:00Z");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-17T09:00:00Z");
        occurrence.Timing.SourceStart.Value.ShouldBe("2026-08-17T09:00:00Z");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-17T13:00:00Z");
        occurrence.Timing.EffectiveEnd!.Value.ShouldBe("2026-08-17T15:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_CancelledRangeOmitsUntilLaterRangeWhileExactOverrideWins()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260814T090000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=5\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260815T090000Z\r\n"
            + "DTSTART:20260815T090000Z\r\nSTATUS:CANCELLED\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260816T090000Z\r\nDTSTART:20260816T200000Z\r\nDURATION:PT1H\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260817T090000Z\r\n"
            + "DTSTART:20260817T130000Z\r\nDURATION:PT1H\r\n",
            "2026-08-14T00:00:00Z",
            "2026-08-19T00:00:00Z");

        result.Items.Select(item => (item.RecurrenceIdentity.Value, item.Timing.EffectiveStart.Value)).ShouldBe([
            ("2026-08-14T09:00:00Z", "2026-08-14T09:00:00Z"),
            ("2026-08-16T09:00:00Z", "2026-08-16T20:00:00Z"),
            ("2026-08-17T09:00:00Z", "2026-08-17T13:00:00Z"),
            ("2026-08-18T09:00:00Z", "2026-08-18T13:00:00Z")
        ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryOccurrencesAsync_DetachedIndividualWinsSameIdentityRangeInEitherComponentOrder(
        bool individualFirst)
    {
        const string range = "BEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260820T100000Z\r\n"
            + "DTSTART:20260820T120000Z\r\nDURATION:PT1H\r\nEND:VEVENT\r\n";
        const string individual = "BEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\n"
            + "DTSTART:20260820T180000Z\r\nDURATION:PT1H\r\nEND:VEVENT\r\n";
        var overrides = individualFirst ? individual + range : range + individual;

        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260814T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\n"
            + overrides[..^"END:VEVENT\r\n".Length],
            "2026-08-20T00:00:00Z",
            "2026-08-21T00:00:00Z");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-20T10:00:00Z");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-20T18:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DetachedEventOverridesAreEnumeratedWithCancellationAndExdatePrecedence()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART:20260814T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "EXDATE:20260823T100000Z\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\nDTSTART:20260821T150000Z\r\nDURATION:PT2H\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260822T100000Z\r\nSTATUS:CANCELLED\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260823T100000Z\r\nDTSTART:20260824T150000Z\r\nDURATION:PT2H\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n");

        var result = await QuerySingleOccurrenceAsync(
            bytes,
            CalendarEntityKind.Event,
            "2026-08-21T00:00:00Z",
            "2026-08-25T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-20T10:00:00Z");
        occurrence.Timing.SourceStart.Value.ShouldBe("2026-08-20T10:00:00Z");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-21T15:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DetachedTodoOverridesWithoutDtstartRemainObservable()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:todo-detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART:20260814T100000Z\r\nDUE:20260814T110000Z\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "EXDATE:20260823T100000Z\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:todo-detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\nDUE:20260821T150000Z\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:todo-detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260822T100000Z\r\nSTATUS:CANCELLED\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:todo-detached\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260823T100000Z\r\nDUE:20260824T150000Z\r\nEND:VTODO\r\n"
            + "END:VCALENDAR\r\n");

        var result = await QuerySingleOccurrenceAsync(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-21T00:00:00Z",
            "2026-08-25T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-20T10:00:00Z");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-21T15:00:00Z");
        occurrence.Timing.EffectiveEnd.ShouldBeNull();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DueOnlyRecurringTodoIsTypedRecurrenceUnevaluable()
    {
        var bytes = TodoWithTemporalLines(
            "due-only",
            "DUE:20260815T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\n");

        var result = await QuerySingleOccurrenceAsync(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-15T00:00:00Z",
            "2026-08-20T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_OldResourceLocalSeriesStartsNearBoundedWindow()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=Private/Zurich:20000101T100000\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY\r\n",
            "2026-08-16T08:30:00Z",
            "2026-08-16T08:45:00Z",
            calendarLines: ResourceLocalFixedZone());

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2026-08-16T10:00:00");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_OldTodoRangeMovedByDueIsIncludedInBoundedSearch()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + ResourceLocalFixedZone()
            + "BEGIN:VTODO\r\nUID:todo-range\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART;TZID=Private/Zurich:20000101T100000\r\nRRULE:FREQ=DAILY\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:todo-range\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;TZID=Private/Zurich;RANGE=THISANDFUTURE:20260801T100000\r\n"
            + "DUE;TZID=Private/Zurich:20260816T100000\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

        var result = await QuerySingleOccurrenceAsync(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-17T07:59:00Z",
            "2026-08-17T08:01:00Z");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-02T10:00:00");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-17T10:00:00");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DateEventIndividualOverrideDefaultsToOneDay()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;VALUE=DATE:20260814\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;VALUE=DATE:20260820\r\nDTSTART;VALUE=DATE:20260822\r\n",
            "2026-08-22T12:00:00Z",
            "2026-08-22T13:00:00Z",
            "UTC");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-20");
        occurrence.Timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-23"));
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DateEventRangeOverrideDefaultsEachOccurrenceToOneDay()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;VALUE=DATE:20260820\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;VALUE=DATE;RANGE=THISANDFUTURE:20260820\r\n"
            + "DTSTART;VALUE=DATE:20260822\r\n",
            "2026-08-23T12:00:00Z",
            "2026-08-23T13:00:00Z",
            "UTC");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-21");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-23");
        occurrence.Timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-24"));
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DateEventOverrideDefaultsToNextLocalDayAcrossDst()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;VALUE=DATE:20260301\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;VALUE=DATE:20260308\r\nDTSTART;VALUE=DATE:20260308\r\n",
            "2026-03-08T00:00:00Z",
            "2026-03-10T00:00:00Z",
            "America/New_York");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedStartUtc!.Value.ShouldBe("2026-03-08T05:00:00Z");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-09T04:00:00Z");
    }

    [Theory]
    [InlineData("P1D", "2026-03-08T14:00:00Z", "2026-03-08T10:00:00")]
    [InlineData("PT24H", "2026-03-08T15:00:00Z", "2026-03-08T11:00:00")]
    public async Task QueryOccurrencesAsync_IanaMasterDistinguishesNominalDayFromAccurateHours(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART;TZID=America/New_York:20260307T100000\r\nDURATION:{duration}\r\n",
            "2026-03-07T14:59:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
        timing.EffectiveEnd!.Value.ShouldBe(expectedEndLocal);
    }

    [Theory]
    [InlineData("P1D", "2026-03-08T14:00:00Z", "2026-03-08T10:00:00")]
    [InlineData("PT24H", "2026-03-08T15:00:00Z", "2026-03-08T11:00:00")]
    public async Task QueryOccurrencesAsync_IanaOverrideDistinguishesNominalDayFromAccurateHours(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=America/New_York:20260301T100000\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;TZID=America/New_York:20260307T100000\r\n"
            + $"DTSTART;TZID=America/New_York:20260307T100000\r\nDURATION:{duration}\r\n",
            "2026-03-07T14:59:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
        timing.EffectiveEnd!.Value.ShouldBe(expectedEndLocal);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DetachedOverrideRetainsAccurateMasterSourceSpanAcrossDst()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=America/New_York:20260306T100000\r\n"
            + "DTEND;TZID=America/New_York:20260307T100000\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;TZID=America/New_York:20260307T100000\r\n"
            + "DTSTART;TZID=America/New_York:20260310T100000\r\nDURATION:PT1H\r\n",
            "2026-03-10T13:59:00Z",
            "2026-03-10T14:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.SourceEnd!.Value.ShouldBe("2026-03-08T11:00:00");
        timing.EffectiveEnd!.Value.ShouldBe("2026-03-10T11:00:00");
    }

    [Theory]
    [InlineData("P1D", "2026-03-29T08:00:00Z", "2026-03-29T10:00:00")]
    [InlineData("PT24H", "2026-03-29T09:00:00Z", "2026-03-29T11:00:00")]
    public async Task QueryOccurrencesAsync_ResourceLocalMasterUsesTheSameDurationArithmetic(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART;TZID=Private/Zurich:20260328T100000\r\nDURATION:{duration}\r\n",
            "2026-03-28T08:59:00Z",
            "2026-03-28T09:01:00Z",
            calendarLines: ResourceLocalDstZone());

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
        timing.EffectiveEnd!.Value.ShouldBe(expectedEndLocal);
    }

    [Theory]
    [InlineData("P1D", "2026-03-29T08:00:00Z", "2026-03-29T10:00:00")]
    [InlineData("PT24H", "2026-03-29T09:00:00Z", "2026-03-29T11:00:00")]
    public async Task QueryOccurrencesAsync_RdatePeriodUsesNominalThenAccurateDurationArithmetic(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=Private/Zurich:20260301T100000\r\nDURATION:PT1H\r\n"
            + $"RDATE;TZID=Private/Zurich;VALUE=PERIOD:20260328T100000/{duration}\r\n",
            "2026-03-28T08:59:00Z",
            "2026-03-28T09:01:00Z",
            calendarLines: ResourceLocalDstZone());

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
        timing.SourceEnd!.Value.ShouldBe(expectedEndLocal);
    }

    [Theory]
    [InlineData("+P1D", "2026-03-29T08:00:00Z", "2026-03-29T10:00:00")]
    [InlineData("+P1W", "2026-04-04T08:00:00Z", "2026-04-04T10:00:00")]
    public async Task QueryOccurrencesAsync_RdatePeriodAcceptsPositiveDurationAndPreservesLexicalValue(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=Europe/Zurich:20260301T100000\r\nDURATION:PT1H\r\n"
            + $"RDATE;TZID=Europe/Zurich;VALUE=PERIOD:20260328T100000/{duration}\r\n",
            "2026-03-28T08:59:00Z",
            "2026-03-28T09:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.SourceDuration.ShouldBe(duration);
        timing.EffectiveDuration.ShouldBe(duration);
        timing.SourceEnd!.Value.ShouldBe(expectedEndLocal);
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
    }

    [Theory]
    [InlineData("DTSTART;TZID=Europe/Zurich:20260328T100000\r\nDURATION:P2147483647D\r\nRRULE:FREQ=DAILY;COUNT=1\r\n")]
    [InlineData("DTSTART;TZID=Europe/Zurich:20260301T100000\r\nDURATION:PT1H\r\nRDATE;TZID=Europe/Zurich;VALUE=PERIOD:20260328T100000/P2147483647D\r\n")]
    public async Task QueryOccurrencesAsync_ExtremeDurationIsTypedRecurrenceUnevaluableWithoutPartialItems(
        string temporalLines)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            temporalLines,
            "2026-03-28T08:59:00Z",
            "2026-03-28T09:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("DURATION:PT0S")]
    [InlineData("DURATION:-PT1H")]
    public async Task QueryOccurrencesAsync_EventDurationMustBeStrictlyPositive(string durationLine)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART:20260816T100000Z\r\n{durationLine}\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("PT0S")]
    [InlineData("-PT1H")]
    public async Task QueryOccurrencesAsync_TodoDurationMustBeStrictlyPositive(string duration)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:todo-duration\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"DTSTART:20260816T100000Z\r\nDURATION:{duration}\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n");

        var result = await QuerySingleOccurrenceAsync(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("P0D")]
    [InlineData("-P1D")]
    public async Task QueryOccurrencesAsync_RdatePeriodDurationMustBeStrictlyPositive(string duration)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260801T100000Z\r\nDURATION:PT1H\r\n"
            + $"RDATE;VALUE=PERIOD:20260816T100000Z/{duration}\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("20260816T100000Z/20260816T100000Z")]
    [InlineData("20260816T100000Z/20260816T095959Z")]
    [InlineData("20260816T100000Z/not-a-date-time")]
    [InlineData("20260816T100000Z/P1Q")]
    public async Task QueryOccurrencesAsync_RdatePeriodMustHaveAValidPositiveSpan(string period)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260801T100000Z\r\nDURATION:PT1H\r\n"
            + $"RDATE;VALUE=PERIOD:{period}\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_RdatePeriodUnknownZoneRemainsTemporalUnresolved()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260801T100000Z\r\nDURATION:PT1H\r\n"
            + "RDATE;TZID=Private/Unknown;VALUE=PERIOD:20260816T100000/20260816T110000\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_PeriodRecurrenceDateInTimeZoneObservanceIsTypedUnevaluable()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=Private/Zone:20260816T100000\r\nDURATION:PT1H\r\n",
            "2026-08-16T07:59:00Z",
            "2026-08-16T08:01:00Z",
            calendarLines: "BEGIN:VTIMEZONE\r\nTZID:Private/Zone\r\nBEGIN:STANDARD\r\n"
                + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
                + "RDATE;VALUE=PERIOD:20261025T030000/20261025T040000\r\n"
                + "END:STANDARD\r\nEND:VTIMEZONE\r\n");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_LocalDateTimeObservanceRecurrenceDateRemainsEffective()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=Custom/Shift:20260816T100000\r\nDURATION:PT30M\r\n",
            "2026-08-16T06:29:00Z",
            "2026-08-16T06:31:00Z",
            calendarLines: "BEGIN:VTIMEZONE\r\nTZID:Custom/Shift\r\nBEGIN:STANDARD\r\n"
                + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\nEND:STANDARD\r\n"
                + "BEGIN:DAYLIGHT\r\nDTSTART:20260816T020000\r\nRDATE:20260816T020000\r\n"
                + "TZOFFSETFROM:+0200\r\nTZOFFSETTO:+0330\r\nEND:DAYLIGHT\r\nEND:VTIMEZONE\r\n");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedStartUtc!.Value.ShouldBe("2026-08-16T06:30:00Z");
    }

    [Theory]
    [InlineData("VEVENT", "DTEND:20260816T110000Z")]
    [InlineData("VTODO", "DUE:20260816T110000Z")]
    public async Task QueryOccurrencesAsync_DurationAndExplicitEndAreMutuallyExclusive(
        string component,
        string endLine)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + $"BEGIN:{component}\r\nUID:exclusive\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"DTSTART:20260816T100000Z\r\n{endLine}\r\nDURATION:PT1H\r\n"
            + $"END:{component}\r\nEND:VCALENDAR\r\n");

        var result = await QuerySingleOccurrenceAsync(
            bytes,
            component == "VEVENT" ? CalendarEntityKind.Event : CalendarEntityKind.Todo,
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("VEVENT")]
    [InlineData("VTODO")]
    public async Task QueryOccurrencesAsync_DurationRequiresDtstart(string component)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + $"BEGIN:{component}\r\nUID:missing-start\r\nDTSTAMP:20260815T120000Z\r\nDURATION:PT1H\r\n"
            + $"END:{component}\r\nEND:VCALENDAR\r\n");

        var result = await QuerySingleOccurrenceAsync(
            bytes,
            component == "VEVENT" ? CalendarEntityKind.Event : CalendarEntityKind.Todo,
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(";RANGE=THISANDFUTURE")]
    public async Task QueryOccurrencesAsync_OverrideDurationAndEndAreMutuallyExclusive(string rangeParameter)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"RECURRENCE-ID{rangeParameter}:20260816T100000Z\r\n"
            + "DTSTART:20260816T100000Z\r\nDTEND:20260816T110000Z\r\nDURATION:PT1H\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("PT24H")]
    [InlineData("P1DT1H")]
    public async Task QueryOccurrencesAsync_DateEventDurationMustUseOnlyDaysOrWeeks(string duration)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART;VALUE=DATE:20260816\r\nDURATION:{duration}\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("P1D")]
    [InlineData("P1W")]
    public async Task QueryOccurrencesAsync_DateEventAcceptsDayOrWeekDuration(string duration)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART;VALUE=DATE:20260816\r\nDURATION:{duration}\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DateTodoDurationMustUseOnlyDaysOrWeeks()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:date-duration\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART;VALUE=DATE:20260816\r\nDURATION:PT24H\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n");

        var result = await QuerySingleOccurrenceAsync(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("P1D")]
    [InlineData("P1W")]
    public async Task QueryOccurrencesAsync_DateTodoAcceptsDayOrWeekDuration(string duration)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:date-duration\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"DTSTART;VALUE=DATE:20260816\r\nDURATION:{duration}\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n");

        var result = await QuerySingleOccurrenceAsync(
            bytes,
            CalendarEntityKind.Todo,
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("")]
    [InlineData(";RANGE=THISANDFUTURE")]
    public async Task QueryOccurrencesAsync_DateOverrideDurationMustUseOnlyDaysOrWeeks(string rangeParameter)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;VALUE=DATE:20260815\r\nDURATION:P1D\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"RECURRENCE-ID;VALUE=DATE{rangeParameter}:20260816\r\n"
            + "DTSTART;VALUE=DATE:20260816\r\nDURATION:P1DT1H\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(";RANGE=THISANDFUTURE")]
    public async Task QueryOccurrencesAsync_OverrideDurationMustBeStrictlyPositive(string rangeParameter)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260801T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + $"RECURRENCE-ID{rangeParameter}:20260816T100000Z\r\n"
            + "DTSTART:20260816T100000Z\r\nDURATION:-PT1H\r\n",
            "2026-08-16T09:59:00Z",
            "2026-08-16T10:01:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("20260307", "20260308", "2026-03-08T05:00:00Z", "2026-03-09", "2026-03-09T01:00:00", "2026-03-09T05:00:00Z")]
    [InlineData("20261031", "20261101", "2026-11-01T04:00:00Z", "2026-11-02", "2026-11-01T23:00:00", "2026-11-02T04:00:00Z")]
    public async Task QueryOccurrencesAsync_RecurringDateExactDurationKeepsSourceDateAndTruthfulEffectiveInstant(
        string masterStart,
        string masterEnd,
        string secondStartUtc,
        string expectedSourceEnd,
        string expectedEffectiveEnd,
        string expectedEndUtc)
    {
        var secondStart = DateTimeOffset.Parse(secondStartUtc);
        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART;VALUE=DATE:{masterStart}\r\nDTEND;VALUE=DATE:{masterEnd}\r\n"
            + "RRULE:FREQ=DAILY;COUNT=2\r\n",
            secondStart.ToString("O"),
            secondStart.AddMinutes(1).ToString("O"),
            "America/New_York");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.SourceEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, expectedSourceEnd));
        timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(
            CalendarTemporalKind.ZonedDateTime,
            expectedEffectiveEnd,
            "America/New_York"));
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_RecurringTodoDateExactDurationUsesTruthfulEffectiveInstant()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:todo-duration\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART;VALUE=DATE:20260307\r\nDUE;VALUE=DATE:20260308\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n");

        var result = await QuerySingleOccurrenceAsync(
            bytes,
            CalendarEntityKind.Todo,
            "2026-03-08T05:00:00Z",
            "2026-03-08T05:01:00Z",
            "America/New_York");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.SourceEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-03-09"));
        timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(
            CalendarTemporalKind.ZonedDateTime,
            "2026-03-09T01:00:00",
            "America/New_York"));
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-09T05:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_RangeDateExactDurationKeepsSourceDateAndTruthfulEffectiveInstant()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;VALUE=DATE:20260307\r\nDTEND;VALUE=DATE:20260308\r\n"
            + "RRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;VALUE=DATE;RANGE=THISANDFUTURE:20260307\r\n"
            + "DTSTART;VALUE=DATE:20260307\r\nDTEND;VALUE=DATE:20260308\r\n",
            "2026-03-08T05:00:00Z",
            "2026-03-08T05:01:00Z",
            "America/New_York");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.SourceEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-03-09"));
        timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(
            CalendarTemporalKind.ZonedDateTime,
            "2026-03-09T01:00:00",
            "America/New_York"));
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-09T05:00:00Z");
    }

    [Theory]
    [InlineData("P1D", "2026-03-08T14:00:00Z", "2026-03-08T10:00:00")]
    [InlineData("PT24H", "2026-03-08T15:00:00Z", "2026-03-08T11:00:00")]
    public async Task QueryOccurrencesAsync_RangeDurationUsesPerInstanceNominalThenAccurateArithmetic(
        string duration,
        string expectedEndUtc,
        string expectedEndLocal)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=America/New_York:20260301T100000\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;TZID=America/New_York;RANGE=THISANDFUTURE:20260307T100000\r\n"
            + $"DTSTART;TZID=America/New_York:20260307T100000\r\nDURATION:{duration}\r\n",
            "2026-03-07T14:59:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedEndUtc!.Value.ShouldBe(expectedEndUtc);
        timing.EffectiveEnd!.Value.ShouldBe(expectedEndLocal);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_RecurringExplicitEndPropagatesExactDurationAcrossDst()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=America/New_York:20260306T100000\r\n"
            + "DTEND;TZID=America/New_York:20260307T100000\r\nRRULE:FREQ=DAILY;COUNT=2\r\n",
            "2026-03-07T15:00:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EffectiveEnd!.Value.ShouldBe("2026-03-08T11:00:00");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-08T15:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_RangeExplicitEndPropagatesExactDurationAfterAnchor()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=America/New_York:20260306T100000\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;TZID=America/New_York;RANGE=THISANDFUTURE:20260306T100000\r\n"
            + "DTSTART;TZID=America/New_York:20260306T100000\r\n"
            + "DTEND;TZID=America/New_York:20260307T100000\r\n",
            "2026-03-07T15:00:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EffectiveEnd!.Value.ShouldBe("2026-03-08T11:00:00");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-08T15:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_RecurringTodoDuePropagatesExactDurationAcrossDst()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:todo-duration\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART;TZID=America/New_York:20260306T100000\r\n"
            + "DUE;TZID=America/New_York:20260307T100000\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n");

        var result = await QuerySingleOccurrenceAsync(
            bytes,
            CalendarEntityKind.Todo,
            "2026-03-07T15:00:00Z",
            "2026-03-07T15:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EffectiveEnd!.Value.ShouldBe("2026-03-08T11:00:00");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-03-08T15:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_WeekDurationRemainsNominalAcrossDst()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=Europe/Zurich:20260328T100000\r\nDURATION:P1W\r\n",
            "2026-03-28T08:59:00Z",
            "2026-03-28T09:01:00Z");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EffectiveEnd!.Value.ShouldBe("2026-04-04T10:00:00");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-04-04T08:00:00Z");
    }

    [Theory]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("P1W1D")]
    [InlineData("P1WT1H")]
    [InlineData("P1H")]
    [InlineData("PT1H30S")]
    [InlineData("P1D1H")]
    [InlineData("P-1D")]
    public void CalendarDurationParser_RejectsNonRfcGrammar(string rawDuration)
    {
        CalendarDurationArithmetic.TryParse(rawDuration, out _).ShouldBeFalse();
    }

    [Fact]
    public void CalendarDurationParser_RecognizesNominalWeek()
    {
        CalendarDurationArithmetic.TryParse("P1W", out var duration).ShouldBeTrue();
        duration.NominalDays.ShouldBe(7);
        duration.Accurate.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("+P1D", 1, 0)]
    [InlineData("-P2D", -2, 0)]
    [InlineData("P0D", 0, 0)]
    [InlineData("-PT0S", 0, 0)]
    [InlineData("PT1H", 0, 3600)]
    [InlineData("PT2M3S", 0, 123)]
    [InlineData("PT3S", 0, 3)]
    [InlineData("P1DT2H3M4S", 1, 7384)]
    public void CalendarDurationParser_PreservesNominalAndAccurateComponents(
        string rawDuration,
        int expectedNominalDays,
        int expectedAccurateSeconds)
    {
        CalendarDurationArithmetic.TryParse(rawDuration, out var duration).ShouldBeTrue();
        duration.NominalDays.ShouldBe(expectedNominalDays);
        duration.Accurate.ShouldBe(TimeSpan.FromSeconds(expectedAccurateSeconds));
    }

    [Fact]
    public async Task QueryOccurrencesAsync_RecurringDateSearchIncludesTwentyFiveHourFallBackSpan()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;VALUE=DATE:20261101\r\nRRULE:FREQ=DAILY;COUNT=1\r\n",
            "2026-11-02T04:30:00Z",
            "2026-11-02T04:45:00Z",
            "America/New_York");

        var timing = result.Items.ShouldHaveSingleItem().Timing;
        timing.EvaluatedStartUtc!.Value.ShouldBe("2026-11-01T04:00:00Z");
        timing.EvaluatedEndUtc!.Value.ShouldBe("2026-11-02T05:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_MultipleRrulesAreTypedRecurrenceUnevaluableWithNoPartialItems()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\n"
            + "RRULE:FREQ=DAILY;COUNT=2\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\n",
            "2026-08-15T00:00:00Z",
            "2026-08-20T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_FloatingComparisonRequiresExplicitEvaluationTimeZoneOnlyWhenEncountered()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/floating.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", EventWithRawStart("floating", string.Empty, "20260816T100000")));
        var scope = CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref));
        var from = DateTimeOffset.Parse("2026-08-16T12:30:00Z");
        var to = DateTimeOffset.Parse("2026-08-16T13:30:00Z");

        var unresolved = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(scope, from, to),
            CancellationToken.None);
        var evaluated = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(scope, from, to, "America/Sao_Paulo"),
            CancellationToken.None);

        unresolved.Code.ShouldBe(CalendarOccurrenceQueryCode.TemporalUnresolved);
        unresolved.Items.ShouldBeEmpty();
        var occurrence = evaluated.Items.ShouldHaveSingleItem();
        occurrence.Timing.EffectiveStart.Kind.ShouldBe(CalendarTemporalKind.FloatingDateTime);
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-16T10:00:00");
        occurrence.Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-08-16T13:00:00Z");
        occurrence.Timing.EvaluationTimeZone.ShouldBe("America/Sao_Paulo");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_RejectsInvalidEvaluationTimeZoneBeforeDiscovery()
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Default,
                DateTimeOffset.Parse("2026-08-16T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-17T00:00:00Z"),
                "Central Standard Time"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.InvalidInput);
        result.Items.ShouldBeEmpty();
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("20260329T023000", "2026-03-29T01:30:00Z")]
    [InlineData("20261025T023000", "2026-10-25T00:30:00Z")]
    public async Task QueryOccurrencesAsync_ExplicitIanaGapUsesPriorOffsetAndOverlapUsesFirstOccurrence(
        string localStart,
        string expectedUtc)
    {
        var expected = DateTimeOffset.Parse(expectedUtc);
        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART;TZID=Europe/Zurich:{localStart}\r\nDURATION:PT30M\r\n",
            expected.AddMinutes(-1).ToString("O"),
            expected.AddMinutes(1).ToString("O"));

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldHaveSingleItem().Timing.EvaluatedStartUtc!.Value.ShouldBe(expectedUtc);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_IanaRruleSkipsGapWithoutConsumingCount()
    {
        const string recurrence = "DTSTART;TZID=Europe/Zurich:20260328T023000\r\n"
            + "DURATION:PT30M\r\nRRULE:FREQ=DAILY;COUNT=2\r\n";
        var gap = await QuerySingleOccurrenceEventAsync(
            recurrence,
            "2026-03-29T01:29:00Z",
            "2026-03-29T01:31:00Z");
        var afterGap = await QuerySingleOccurrenceEventAsync(
            recurrence,
            "2026-03-30T00:29:00Z",
            "2026-03-30T00:31:00Z");

        gap.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        gap.Items.ShouldBeEmpty();
        afterGap.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2026-03-30T02:30:00");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_ExplicitFloatingGapUsesPriorEvaluationZoneOffset()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260329T023000\r\nDURATION:PT30M\r\n",
            "2026-03-29T01:29:00Z",
            "2026-03-29T01:31:00Z",
            "Europe/Zurich");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldHaveSingleItem().Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-03-29T01:30:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_FloatingRruleSkipsEvaluationZoneGapWithoutConsumingCount()
    {
        const string recurrence = "DTSTART:20260328T023000\r\n"
            + "DURATION:PT30M\r\nRRULE:FREQ=DAILY;COUNT=2\r\n";
        var gap = await QuerySingleOccurrenceEventAsync(
            recurrence,
            "2026-03-29T01:29:00Z",
            "2026-03-29T01:31:00Z",
            "Europe/Zurich");
        var afterGap = await QuerySingleOccurrenceEventAsync(
            recurrence,
            "2026-03-30T00:29:00Z",
            "2026-03-30T00:31:00Z",
            "Europe/Zurich");

        gap.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        gap.Items.ShouldBeEmpty();
        afterGap.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2026-03-30T02:30:00");
    }

    [Theory]
    [InlineData("20260329T023000", "2026-03-29T01:30:00Z")]
    [InlineData("20261025T023000", "2026-10-25T00:30:00Z")]
    public async Task QueryOccurrencesAsync_ExplicitResourceLocalGapAndOverlapFollowRfc5545(
        string localStart,
        string expectedUtc)
    {
        var expected = DateTimeOffset.Parse(expectedUtc);
        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART;TZID=Private/Zurich:{localStart}\r\nDURATION:PT30M\r\n",
            expected.AddMinutes(-1).ToString("O"),
            expected.AddMinutes(1).ToString("O"),
            calendarLines: ResourceLocalDstZone());

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldHaveSingleItem().Timing.EvaluatedStartUtc!.Value.ShouldBe(expectedUtc);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_ResourceLocalRruleSkipsGapWithoutConsumingCount()
    {
        const string recurrence = "DTSTART;TZID=Private/Zurich:20260328T023000\r\n"
            + "DURATION:PT30M\r\nRRULE:FREQ=DAILY;COUNT=2\r\n";
        var gap = await QuerySingleOccurrenceEventAsync(
            recurrence,
            "2026-03-29T01:29:00Z",
            "2026-03-29T01:31:00Z",
            calendarLines: ResourceLocalDstZone());
        var afterGap = await QuerySingleOccurrenceEventAsync(
            recurrence,
            "2026-03-30T00:29:00Z",
            "2026-03-30T00:31:00Z",
            calendarLines: ResourceLocalDstZone());

        gap.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        gap.Items.ShouldBeEmpty();
        afterGap.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2026-03-30T02:30:00");
    }

    [Theory]
    [InlineData("Europe/Zurich", null, false)]
    [InlineData("Private/Zurich", null, true)]
    [InlineData(null, "Europe/Zurich", false)]
    public async Task QueryOccurrencesAsync_RruleOverlapUsesFirstOccurrenceExactlyOnce(
        string? timeZoneId,
        string? evaluationTimeZone,
        bool resourceLocal)
    {
        var parameter = timeZoneId is null ? string.Empty : $";TZID={timeZoneId}";
        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART{parameter}:20261024T023000\r\nDURATION:PT30M\r\nRRULE:FREQ=DAILY;COUNT=2\r\n",
            "2026-10-25T00:29:00Z",
            "2026-10-25T00:31:00Z",
            evaluationTimeZone,
            resourceLocal ? ResourceLocalDstZone() : string.Empty);

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-10-25T00:30:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_IanaDailyRecurrencePreservesWallTimeAcrossDst()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=America/New_York:20260307T100000\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=3\r\n",
            "2026-03-08T14:30:00Z",
            "2026-03-08T14:45:00Z");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.ShouldBe(new CalendarTemporalValue(
            CalendarTemporalKind.ZonedDateTime,
            "2026-03-08T10:00:00",
            "America/New_York"));
        occurrence.Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-03-08T14:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_LeapRuleFindsNextLeapDayWithoutServerExpansion()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20240229T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=YEARLY;COUNT=3\r\n",
            "2028-02-29T10:30:00Z",
            "2028-02-29T10:45:00Z");

        result.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2028-02-29T10:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_UnknownNamedZoneIsTypedTemporalUnresolved()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=Private/Office:20260816T100000\r\nDURATION:PT1H\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_ExdateSuppressedUnknownZoneOverrideDoesNotRequireResolution()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "EXDATE:20260820T100000Z\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\n"
            + "DTSTART;TZID=Mars/Base:20260821T100000\r\nDURATION:PT1H\r\n",
            "2026-08-20T00:00:00Z",
            "2026-08-22T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_CancelledUnknownZoneOverrideDoesNotRequireResolution()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\n"
            + "DTSTART;TZID=Mars/Base:20260821T100000\r\nSTATUS:CANCELLED\r\n",
            "2026-08-20T00:00:00Z",
            "2026-08-22T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_ActiveUnknownZoneOverrideRemainsTypedTemporalUnresolved()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\n"
            + "DTSTART;TZID=Mars/Base:20260821T100000\r\nDURATION:PT1H\r\n",
            "2026-08-20T00:00:00Z",
            "2026-08-22T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_ConflictingResourceLocalZonesAreTypedTemporalUnresolved()
    {
        const string zone = "BEGIN:VTIMEZONE\r\nTZID:Private/Office\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
            + "END:STANDARD\r\nEND:VTIMEZONE\r\n";
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=Private/Office:20260816T100000\r\nDURATION:PT1H\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z",
            calendarLines: zone + zone);

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.TemporalUnresolved);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_ResourceLocalRecurringZoneIsEvaluatedWithoutIanaFallback()
    {
        const string zone = "BEGIN:VTIMEZONE\r\nTZID:Private/Office\r\nBEGIN:STANDARD\r\n"
            + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
            + "END:STANDARD\r\nEND:VTIMEZONE\r\n";
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;TZID=Private/Office:20260815T100000\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=2\r\n",
            "2026-08-16T08:30:00Z",
            "2026-08-16T08:45:00Z",
            calendarLines: zone);

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.TimeZoneId.ShouldBe("Private/Office");
        occurrence.Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-08-16T08:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_UnambiguousResourceLocalTimeZonePrecedesIanaDefinition()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/local-zone.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", ResourceLocalZoneEvent()));

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse("2026-08-16T07:30:00Z"),
                DateTimeOffset.Parse("2026-08-16T08:30:00Z")),
            CancellationToken.None);

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.Timing.EffectiveStart.Kind.ShouldBe(CalendarTemporalKind.ZonedDateTime);
        occurrence.Timing.EffectiveStart.TimeZoneId.ShouldBe("America/New_York");
        occurrence.Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-08-16T08:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_RdatePeriodSuppliesOccurrenceSpecificSpan()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260814T100000Z\r\nDURATION:PT1H\r\n"
            + "RDATE;VALUE=PERIOD:20260816T100000Z/20260816T130000Z\r\n",
            "2026-08-16T12:30:00Z",
            "2026-08-16T12:45:00Z");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-16T10:00:00Z");
        occurrence.Timing.SourceEnd!.Value.ShouldBe("2026-08-16T13:00:00Z");
        occurrence.Timing.EffectiveEnd!.Value.ShouldBe("2026-08-16T13:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_RdatePeriodDeduplicatesAndOverrideUsesOccurrenceSpecificSourceSpan()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "RDATE;VALUE=PERIOD:20260816T100000Z/PT3H\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260816T100000Z\r\nDTSTART:20260816T150000Z\r\nDURATION:PT2H\r\n",
            "2026-08-16T15:30:00Z",
            "2026-08-16T15:45:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-16T10:00:00Z");
        occurrence.Timing.SourceEnd!.Value.ShouldBe("2026-08-16T13:00:00Z");
        occurrence.Timing.SourceDuration.ShouldBe("PT3H");
        occurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-16T15:00:00Z");
        occurrence.Timing.EffectiveEnd!.Value.ShouldBe("2026-08-16T17:00:00Z");
        occurrence.Timing.EffectiveDuration.ShouldBe("PT2H");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_ExdateSuppressesDuplicateRdatePeriodAndItsOverride()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART:20260815T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
            + "RDATE;VALUE=PERIOD:20260816T100000Z/PT3H\r\nEXDATE:20260816T100000Z\r\n"
            + "END:VEVENT\r\nBEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID:20260816T100000Z\r\nDTSTART:20260816T150000Z\r\nDURATION:PT2H\r\n",
            "2026-08-16T00:00:00Z",
            "2026-08-17T00:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DateOnlyEventDefaultsToOneLocalDay()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;VALUE=DATE:20260816\r\n",
            "2026-08-17T03:30:00Z",
            "2026-08-17T03:45:00Z",
            "America/New_York");

        var occurrence = result.Items.ShouldHaveSingleItem();
        occurrence.RecurrenceIdentity.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-16"));
        occurrence.Timing.EffectiveEnd.ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.Date, "2026-08-17"));
        occurrence.Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-08-16T04:00:00Z");
        occurrence.Timing.EvaluatedEndUtc!.Value.ShouldBe("2026-08-17T04:00:00Z");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DateOnlyTodoStartAndDueAreLocalPoints()
    {
        const string calendarHref = "https://cal.example/todos/";
        var startHref = $"{calendarHref}start.ics";
        var dueHref = $"{calendarHref}due.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([startHref, dueHref]);
        client.GetCalendarResourceAsync(startHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(startHref, "\"s1\"", TodoWithTemporalLines("start", "DTSTART;VALUE=DATE:20260816\r\n")));
        client.GetCalendarResourceAsync(dueHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(dueHref, "\"d1\"", TodoWithTemporalLines("due", "DUE;VALUE=DATE:20260816\r\n")));

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse("2026-08-16T03:59:59Z"),
                DateTimeOffset.Parse("2026-08-16T04:00:01Z"),
                "America/New_York"),
            CancellationToken.None);

        result.Items.Select(item => item.Snapshot.Projection.EntityUid).ShouldBe(["due", "start"]);
        result.Items.ShouldAllBe(item => item.Timing.EffectiveStart.Kind == CalendarTemporalKind.Date);
        result.Items.ShouldAllBe(item => item.Timing.EvaluatedStartUtc!.Value == "2026-08-16T04:00:00Z");
        result.Items.ShouldAllBe(item => item.Timing.EffectiveEnd == null);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DateOnlyExdateListResolvesEachIdentity()
    {
        var result = await QuerySingleOccurrenceEventAsync(
            "DTSTART;VALUE=DATE:20260815\r\nRRULE:FREQ=DAILY;COUNT=4\r\n"
            + "EXDATE;VALUE=DATE:20260816,20260817\r\n",
            "2026-08-15T00:00:00Z",
            "2026-08-19T00:00:00Z",
            "UTC");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.Select(item => item.RecurrenceIdentity.Value).ShouldBe(["2026-08-15", "2026-08-18"]);
    }

    [Theory]
    [InlineData(1999, CalendarOccurrenceQueryCode.Success, 1999, null)]
    [InlineData(2000, CalendarOccurrenceQueryCode.Success, 2000, null)]
    [InlineData(2001, CalendarOccurrenceQueryCode.LimitExhausted, 0, 2001)]
    public async Task QueryOccurrencesAsync_EnforcesExactPerEntityOccurrenceBoundaryWithZeroPartial(
        int occurrenceCount,
        CalendarOccurrenceQueryCode expectedCode,
        int expectedItems,
        int? expectedObservedCount)
    {
        var result = await QueryOccurrenceCountsAsync(occurrenceCount);

        result.Code.ShouldBe(expectedCode);
        result.Items.Count.ShouldBe(expectedItems);
        result.Limits?.OccurrenceCount.ShouldBe(expectedObservedCount);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_PerEntityBoundaryIncludesUniqueRdatePeriods()
    {
        var start = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var periods = string.Join(',', Enumerable.Range(0, 2001).Select(index =>
            $"{start.AddSeconds(index):yyyyMMdd'T'HHmmss'Z'}/PT1S"));

        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART:20200101T120000Z\r\nDURATION:PT1S\r\nRDATE;VALUE=PERIOD:{periods}\r\n",
            "2026-08-15T12:00:00Z",
            "2026-08-15T13:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.LimitExhausted);
        result.Items.ShouldBeEmpty();
        result.Limits!.OccurrenceCount.ShouldBe(2001);
    }

    [Theory]
    [InlineData(2000, CalendarOccurrenceQueryCode.Success, null)]
    [InlineData(2001, CalendarOccurrenceQueryCode.LimitExhausted, 2001)]
    public async Task QueryOccurrencesAsync_PeriodBoundaryCountsUniqueDerivedWorkOutsideWindow(
        int periodCount,
        CalendarOccurrenceQueryCode expectedCode,
        int? expectedObservedCount)
    {
        var start = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var periods = string.Join(',', Enumerable.Range(0, periodCount).Select(index =>
            $"{start.AddSeconds(index):yyyyMMdd'T'HHmmss'Z'}/PT1S"));

        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART:20200101T120000Z\r\nDURATION:PT1S\r\nRDATE;VALUE=PERIOD:{periods}\r\n",
            "2027-08-15T12:00:00Z",
            "2027-08-15T13:00:00Z");

        result.Code.ShouldBe(expectedCode);
        result.Items.ShouldBeEmpty();
        result.Limits?.OccurrenceCount.ShouldBe(expectedObservedCount);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DuplicatePeriodIdentityDoesNotInflateDerivedWork()
    {
        var start = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var unique = Enumerable.Range(0, 2000)
            .Select(index => $"{start.AddSeconds(index):yyyyMMdd'T'HHmmss'Z'}/PT1S")
            .ToArray();
        var periods = string.Join(',', unique.Append(unique[^1]));

        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART:20200101T120000Z\r\nDURATION:PT1S\r\nRDATE;VALUE=PERIOD:{periods}\r\n",
            "2027-08-15T12:00:00Z",
            "2027-08-15T13:00:00Z");

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(2000, CalendarOccurrenceQueryCode.Success, null)]
    [InlineData(2001, CalendarOccurrenceQueryCode.LimitExhausted, 2001)]
    public async Task QueryOccurrencesAsync_DetachedOverrideBoundaryCountsUniqueDerivedWorkOutsideWindow(
        int overrideCount,
        CalendarOccurrenceQueryCode expectedCode,
        int? expectedObservedCount)
    {
        var result = await QuerySingleOccurrenceAsync(
            EventWithDetachedOverrides(overrideCount),
            CalendarEntityKind.Event,
            "2027-08-15T12:00:00Z",
            "2027-08-15T13:00:00Z");

        result.Code.ShouldBe(expectedCode);
        result.Items.ShouldBeEmpty();
        result.Limits?.OccurrenceCount.ShouldBe(expectedObservedCount);
    }

    [Theory]
    [InlineData(500, CalendarOccurrenceQueryCode.Success, 1500, null)]
    [InlineData(501, CalendarOccurrenceQueryCode.LimitExhausted, 0, 2001)]
    public async Task QueryOccurrencesAsync_PerEntityBoundaryUnionsRrulePeriodAndDetachedIdentities(
        int detachedCount,
        CalendarOccurrenceQueryCode expectedCode,
        int expectedItems,
        int? expectedObservedCount)
    {
        var result = await QuerySingleOccurrenceAsync(
            EventWithCompositeDerivedWork(detachedCount),
            CalendarEntityKind.Event,
            "2026-08-15T12:00:00Z",
            "2026-08-15T13:00:00Z");

        result.Code.ShouldBe(expectedCode);
        result.Items.Count.ShouldBe(expectedItems);
        result.Limits?.OccurrenceCount.ShouldBe(expectedObservedCount);
    }

    [Theory]
    [InlineData(4999, CalendarOccurrenceQueryCode.Success, 4999, null)]
    [InlineData(5000, CalendarOccurrenceQueryCode.Success, 5000, null)]
    [InlineData(5001, CalendarOccurrenceQueryCode.LimitExhausted, 0, 5001)]
    public async Task QueryOccurrencesAsync_EnforcesExactTotalOccurrenceBoundaryWithZeroPartial(
        int totalOccurrences,
        CalendarOccurrenceQueryCode expectedCode,
        int expectedItems,
        int? expectedObservedCount)
    {
        var result = await QueryOccurrenceCountsAsync(2000, 2000, totalOccurrences - 4000);

        result.Code.ShouldBe(expectedCode);
        result.Items.Count.ShouldBe(expectedItems);
        result.Limits?.OccurrenceCount.ShouldBe(expectedObservedCount);
    }

    [Theory]
    [InlineData("20531230T100000Z", CalendarOccurrenceQueryCode.Success)]
    [InlineData("20531231T100000Z", CalendarOccurrenceQueryCode.Success)]
    [InlineData("20540101T100000Z", CalendarOccurrenceQueryCode.LimitExhausted)]
    public async Task QueryOccurrencesAsync_EnforcesExactUnmatchedIncrementBoundaryWithoutPartialOrInventedOccurrenceCount(
        string until,
        CalendarOccurrenceQueryCode expectedCode)
    {
        var result = await QuerySingleOccurrenceEventAsync(
            $"DTSTART:20260816T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;BYMONTH=2;BYMONTHDAY=30;UNTIL={until}\r\n",
            "2026-08-16T10:00:00Z",
            "2026-08-16T11:00:00Z");

        result.Code.ShouldBe(expectedCode);
        result.Items.ShouldBeEmpty();
        result.Limits.ShouldBeNull();
    }

    [Fact]
    public async Task QueryOccurrencesAsync_OrdersByEffectiveStartCalendarHrefUidThenRecurrenceIdentity()
    {
        const string firstCalendar = "https://cal.example/a/";
        const string laterCalendar = "https://cal.example/z/";
        var recurringHref = $"{firstCalendar}recurring.ics";
        var laterUidHref = $"{firstCalendar}single.ics";
        var laterCalendarHref = $"{laterCalendar}single.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            EntityCalendar(laterCalendar, "Z", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised),
            EntityCalendar(firstCalendar, "A", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)
        ]);
        client.QueryCalendarResourceHrefsAsync(firstCalendar, CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([laterUidHref, recurringHref]);
        client.QueryCalendarResourceHrefsAsync(laterCalendar, CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([laterCalendarHref]);
        client.QueryCalendarResourceHrefsAsync(Arg.Any<string>(), CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCalendarResourceAsync(recurringHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(recurringHref, "\"r1\"", SameEffectiveStartRecurrence("a")));
        client.GetCalendarResourceAsync(laterUidHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(laterUidHref, "\"r2\"", Event("z", "20260820T100000Z", "Z")));
        client.GetCalendarResourceAsync(laterCalendarHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(laterCalendarHref, "\"r3\"", Event("0", "20260820T100000Z", "Zero")));

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.All,
                DateTimeOffset.Parse("2026-08-20T09:59:00Z"),
                DateTimeOffset.Parse("2026-08-20T10:01:00Z")),
            CancellationToken.None);

        result.Items.Select(item => (
            item.Snapshot.CalendarHref,
            item.Snapshot.Projection.EntityUid,
            item.RecurrenceIdentity.Value)).ShouldBe([
                (firstCalendar, "a", "2026-08-15T10:00:00Z"),
                (firstCalendar, "a", "2026-08-16T10:00:00Z"),
                (firstCalendar, "z", "2026-08-20T10:00:00Z"),
                (laterCalendar, "0", "2026-08-20T10:00:00Z")
            ]);
    }

    [Fact]
    public async Task QueryOccurrencesAsync_DirectCandidate404ContinuesWithDiagnosticInsteadOfWholeQueryFailure()
    {
        const string calendarHref = "https://cal.example/events/";
        var vanished = $"{calendarHref}vanished.ics";
        var current = $"{calendarHref}current.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([vanished, current]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCalendarResourceAsync(vanished, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.GetCalendarResourceAsync(current, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(current, "\"r1\"", Event("current", "20260816T100000Z", "Current")));

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse("2026-08-16T09:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T11:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.Success);
        result.Items.ShouldHaveSingleItem().Snapshot.ResourceHref.ShouldBe(current);
        result.Diagnostics.Select(item => item.Code).ShouldContain("resource_disappeared_during_query");
    }

    [Fact]
    public async Task QueryOccurrencesAsync_CandidateProtocolFailureIsAnExecutionFailure()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/invalid.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw new CalendarDiscoveryProtocolException("candidate failure"));

        var result = await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse("2026-08-16T09:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T11:00:00Z")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarOccurrenceQueryCode.UpstreamProtocolError);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryTodosAsync_NormalizesCompletionBeforeFiltering()
    {
        const string calendarHref = "https://cal.example/todos/";
        var hrefs = new[]
        {
            $"{calendarHref}open.ics",
            $"{calendarHref}completed.ics",
            $"{calendarHref}cancelled.ics",
            $"{calendarHref}conflict.ics"
        };
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo, null, null, Arg.Any<CancellationToken>())
            .Returns(hrefs);
        client.GetCalendarResourceAsync(hrefs[0], Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(hrefs[0], "\"r1\"", TodoWithTemporalLines("open", "SUMMARY:Open\r\n")));
        client.GetCalendarResourceAsync(hrefs[1], Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(hrefs[1], "\"r2\"", TodoWithTemporalLines("completed", "STATUS:COMPLETED\r\nPERCENT-COMPLETE:100\r\n")));
        client.GetCalendarResourceAsync(hrefs[2], Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(hrefs[2], "\"r3\"", TodoWithTemporalLines("cancelled", "STATUS:CANCELLED\r\n")));
        client.GetCalendarResourceAsync(hrefs[3], Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(hrefs[3], "\"r4\"", TodoWithTemporalLines("conflict", "STATUS:IN-PROCESS\r\nPERCENT-COMPLETE:100\r\n")));

        var result = await sut.QueryTodosAsync(
            new CalendarTodoQuery(CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref))),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarTodoQueryCode.Success);
        result.Items.ShouldHaveSingleItem().Completion.State.ShouldBe(CalendarTodoCompletionState.Open);
        result.ExcludedIndeterminateCount.ShouldBe(1);
        await client.Received(1).QueryCalendarResourceHrefsAsync(
            calendarHref, CalendarEntityKind.Todo, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTodosAsync_WindowReturnsRecurringOccurrenceAndUndatedLane()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string recurringHref = "https://cal.example/todos/recurring.ics";
        const string undatedHref = "https://cal.example/todos/undated.ics";
        var from = DateTimeOffset.Parse("2026-08-15T00:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-17T00:00:00Z");
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref, CalendarEntityKind.Todo, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([recurringHref, undatedHref]);
        client.GetCalendarResourceAsync(recurringHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(recurringHref, "\"r1\"", TodoWithTemporalLines(
                "recurring", "DTSTART:20260815T100000Z\r\nDUE:20260815T103000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\n")));
        client.GetCalendarResourceAsync(undatedHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(undatedHref, "\"r2\"", TodoWithTemporalLines("undated", "SUMMARY:Undated\r\n")));

        var result = await sut.QueryTodosAsync(
            new CalendarTodoQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                [CalendarTodoCompletionState.Open],
                from,
                to),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarTodoQueryCode.Success);
        result.Items.Select(item => item.ResultKind).ShouldBe([
            CalendarTodoQueryResultKind.Occurrence,
            CalendarTodoQueryResultKind.Occurrence,
            CalendarTodoQueryResultKind.Entity]);
        result.Items[0].Occurrence.ShouldNotBeNull();
        result.Items[0].EvaluatedDueUtc.ShouldBe(DateTimeOffset.Parse("2026-08-15T10:30:00Z"));
    }

    [Fact]
    public async Task QueryTodosAsync_WindowIncludesCancelledRangeOverrideWhenRequested()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VTODO\r\nUID:cancelled-range\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART:20260819T100000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:cancelled-range\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260820T100000Z\r\nSTATUS:CANCELLED\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n");

        var result = await QuerySingleTodoAsync(bytes, new CalendarTodoQuery(
            CalendarEntityScope.Selected(new CalendarReference(Href: "https://cal.example/todos/")),
            [CalendarTodoCompletionState.Cancelled],
            DateTimeOffset.Parse("2026-08-19T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-23T00:00:00Z")));

        result.Code.ShouldBe(CalendarTodoQueryCode.Success);
        result.Items.Select(item => item.Occurrence!.RecurrenceIdentity.Value)
            .ShouldBe(["2026-08-20T10:00:00Z", "2026-08-21T10:00:00Z", "2026-08-22T10:00:00Z"]);
        result.Items.ShouldAllBe(item => item.Completion.State == CalendarTodoCompletionState.Cancelled);
    }

    [Fact]
    public async Task QueryTodosAsync_WindowDoesNotInventDueForRecurringStartOnlyTodo()
    {
        var bytes = TodoWithTemporalLines(
            "start-only-recurring",
            "DTSTART:20260819T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\n");

        var result = await QuerySingleTodoAsync(bytes, new CalendarTodoQuery(
            CalendarEntityScope.Selected(new CalendarReference(Href: "https://cal.example/todos/")),
            [CalendarTodoCompletionState.Open],
            DateTimeOffset.Parse("2026-08-19T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-22T00:00:00Z"),
            DueFrom: DateTimeOffset.Parse("2026-08-19T00:00:00Z"),
            DueTo: DateTimeOffset.Parse("2026-08-22T00:00:00Z")));

        result.Code.ShouldBe(CalendarTodoQueryCode.Success);
        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryTodosAsync_RejectsDefaultScopeAndUnpairedWindowBeforeDiscovery()
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());

        var defaultScope = await sut.QueryTodosAsync(new CalendarTodoQuery(CalendarEntityScope.Default), CancellationToken.None);
        var unpaired = await sut.QueryTodosAsync(
            new CalendarTodoQuery(CalendarEntityScope.All, From: DateTimeOffset.UtcNow), CancellationToken.None);

        defaultScope.Code.ShouldBe(CalendarTodoQueryCode.InvalidInput);
        unpaired.Code.ShouldBe(CalendarTodoQueryCode.InvalidInput);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryTodosAsync_DueWindowExcludesUndatedAndUsesHalfOpenBounds()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string firstHref = "https://cal.example/todos/first.ics";
        const string boundaryHref = "https://cal.example/todos/boundary.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo, null, null, Arg.Any<CancellationToken>())
            .Returns([firstHref, boundaryHref]);
        client.GetCalendarResourceAsync(firstHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(firstHref, "\"r1\"", TodoWithTemporalLines("first", "DUE:20260816T100000Z\r\n")));
        client.GetCalendarResourceAsync(boundaryHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(boundaryHref, "\"r2\"", TodoWithTemporalLines("boundary", "DUE:20260817T100000Z\r\n")));

        var result = await sut.QueryTodosAsync(
            new CalendarTodoQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                [CalendarTodoCompletionState.Open],
                DueFrom: DateTimeOffset.Parse("2026-08-16T00:00:00Z"),
                DueTo: DateTimeOffset.Parse("2026-08-17T00:00:00Z")),
            CancellationToken.None);

        result.Items.ShouldHaveSingleItem().Snapshot.Projection.EntityUid.ShouldBe("first");
    }

    [Fact]
    public async Task QueryTodosAsync_ExplicitStatesIncludeCancelledAndCompleted()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string href = "https://cal.example/todos/completed.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo, null, null, Arg.Any<CancellationToken>())
            .Returns([href]);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(href, "\"r1\"", TodoWithTemporalLines("cancelled", "STATUS:CANCELLED\r\n")));

        var result = await sut.QueryTodosAsync(
            new CalendarTodoQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                [CalendarTodoCompletionState.Cancelled]),
            CancellationToken.None);

        result.Items.ShouldHaveSingleItem().Completion.State.ShouldBe(CalendarTodoCompletionState.Cancelled);
    }

    [Fact]
    public async Task QueryTodosAsync_UnknownTemporalRequiresEvaluationContext()
    {
        const string calendarHref = "https://cal.example/todos/";
        const string href = "https://cal.example/todos/floating.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)]);
        client.QueryCalendarResourceHrefsAsync(calendarHref, CalendarEntityKind.Todo, null, null, Arg.Any<CancellationToken>())
            .Returns([href]);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(href, "\"r1\"", TodoWithTemporalLines("floating", "DUE:20260816T100000\r\n")));

        var result = await sut.QueryTodosAsync(
            new CalendarTodoQuery(CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)), [CalendarTodoCompletionState.Open]),
            CancellationToken.None);

        result.Items.ShouldBeEmpty();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "temporal_unresolved");
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable, CalendarTodoQueryCode.ConcurrencyUnavailable)]
    [InlineData(CalendarResourceReadCode.PayloadTooLarge, CalendarTodoQueryCode.PayloadTooLarge)]
    [InlineData(CalendarResourceReadCode.UpstreamProtocolError, CalendarTodoQueryCode.UpstreamProtocolError)]
    public async Task QueryTodosAsync_MapsOccurrenceSnapshotReadFailures(
        CalendarResourceReadCode readCode,
        CalendarTodoQueryCode expectedCode)
    {
        const string calendarHref = "https://cal.example/todos/";
        const string entityHref = "https://cal.example/todos/entity.ics";
        const string failedHref = "https://cal.example/todos/failed.ics";
        var from = DateTimeOffset.Parse("2026-08-15T00:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-17T00:00:00Z");
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "To-dos", EntityKindSupport.Advertised, EntityKindSupport.Advertised)]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                Arg.Any<CalendarEntityKind>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<DateTimeOffset?>(2) is null ? [entityHref] : [failedHref]);
        client.GetCalendarResourceAsync(entityHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(entityHref, "\"entity\"", TodoWithTemporalLines("entity", "SUMMARY:Entity\r\n")));
        client.GetCalendarResourceAsync(failedHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(
                readCode,
                failedHref,
                ObservedByteCount: readCode == CalendarResourceReadCode.PayloadTooLarge ? 4_194_305 : null));

        var result = await sut.QueryTodosAsync(
            new CalendarTodoQuery(CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)), [CalendarTodoCompletionState.Open], from, to),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(CalendarEntityScopeMode.Default, null, null, null, null)]
    [InlineData(CalendarEntityScopeMode.Selected, "", null, null, null)]
    [InlineData(CalendarEntityScopeMode.All, null, "2026-08-15T00:00:00+01:00", "2026-08-16T00:00:00Z", null)]
    [InlineData(CalendarEntityScopeMode.All, null, "2026-08-15T00:00:00Z", "2027-08-17T00:00:00Z", null)]
    [InlineData(CalendarEntityScopeMode.All, null, null, null, "America/Sao_Paulo")]
    [InlineData(CalendarEntityScopeMode.All, null, null, null, "Not/AZone")]
    public async Task QueryTodosAsync_RejectsInvalidCompletionQueryShapes(
        CalendarEntityScopeMode scopeMode,
        string? selectorName,
        string? fromText,
        string? toText,
        string? evaluationTimeZone)
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        var scope = scopeMode switch
        {
            CalendarEntityScopeMode.Default => CalendarEntityScope.Default,
            CalendarEntityScopeMode.Selected => CalendarEntityScope.Selected(new CalendarReference(Name: selectorName)),
            _ => CalendarEntityScope.All
        };
        DateTimeOffset? from = fromText is null ? null : DateTimeOffset.Parse(fromText);
        DateTimeOffset? to = toText is null ? null : DateTimeOffset.Parse(toText);
        var result = await sut.QueryTodosAsync(
            new CalendarTodoQuery(scope, [CalendarTodoCompletionState.Open, CalendarTodoCompletionState.Open], from, to, evaluationTimeZone),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarTodoQueryCode.InvalidInput);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

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
    [InlineData("20260329T023000", "2026-03-29T01:30:00Z")]
    [InlineData("20261025T023000", "2026-10-25T00:30:00Z")]
    public async Task QueryEntitiesAsync_ExplicitIanaGapAndOverlapFollowRfc5545(
        string localStart,
        string expectedUtc)
    {
        var expected = DateTimeOffset.Parse(expectedUtc);
        var result = await QuerySingleEventAsync(
            $"DTSTART;TZID=Europe/Zurich:{localStart}\r\nDURATION:PT30M\r\n",
            expected.AddMinutes(-1).ToString("O"),
            expected.AddMinutes(1).ToString("O"));

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldHaveSingleItem();
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
    [InlineData("20260329T023000", "2026-03-29T01:30:00Z")]
    [InlineData("20261025T023000", "2026-10-25T00:30:00Z")]
    public async Task QueryEntitiesAsync_ExplicitLocalTimeZoneGapAndOverlapFollowRfc5545(
        string localStart,
        string expectedUtc)
    {
        var expected = DateTimeOffset.Parse(expectedUtc);
        var result = await QuerySingleEventAsync(
            $"DTSTART;TZID=Custom/Dst:{localStart}\r\nDURATION:PT30M\r\n",
            expected.AddMinutes(-1).ToString("O"),
            expected.AddMinutes(1).ToString("O"),
            ResourceLocalDstZone().Replace("Private/Zurich", "Custom/Dst", StringComparison.Ordinal));

        result.Code.ShouldBe(CalendarEntityQueryCode.Success);
        result.Items.ShouldHaveSingleItem();
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
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nSUMMARY:Standup\r\nDESCRIPTION:Folded\r\n exactly\r\n\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
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
        result.Snapshot.Diagnostics.ShouldBeEmpty();
        var description = result.Snapshot.CalendarProperties.Single(property => property.Name == "DESCRIPTION");
        description.RawEncodedValue.ShouldBe("Foldedexactly");
        description.OriginalSlice.ShouldBe("DESCRIPTION:Folded\r\n exactly\r\n");
        description.ComponentPath.Select(component => component.Name).ShouldBe(["VCALENDAR", "VEVENT"]);
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
    public async Task ResolveDefaultCalendarAsync_AmbiguousCaseInsensitiveNameReturnsCompleteAuthorizedCalendarEvidence()
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                CalendarHrefs = "https://cal.example/work/,https://cal.example/archive/",
                DefaultTodoCalendarName = "WORK"
            }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            EntityCalendar("https://cal.example/work/", " Work ", EntityKindSupport.Advertised, EntityKindSupport.Unknown),
            EntityCalendar("https://cal.example/archive/", "work", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)
        ]);

        var result = await sut.ResolveDefaultCalendarAsync(CalendarEntityKind.Todo, CancellationToken.None);

        result.Code.ShouldBe(CalendarSelectionCode.Ambiguous);
        result.Candidates.Select(candidate => (
            candidate.DisplayName,
            candidate.Href,
            candidate.EventSupport,
            candidate.TodoSupport)).ShouldBe([
                ("work", "https://cal.example/archive/", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised),
                (" Work ", "https://cal.example/work/", EntityKindSupport.Advertised, EntityKindSupport.Unknown)
            ]);
    }

    [Fact]
    public async Task GetCalendarsAsync_AppliesExactScopeAndPreservesDiscoveryEvidence()
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/a/,https://cal.example/missing/,https://cal.example/a/"
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
            .ShouldBe(["duplicate_calendar_href", "calendar_href_not_found"]);
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

    [Theory]
    [InlineData(256, false)]
    [InlineData(257, true)]
    public async Task GetCalendarsAsync_DeduplicatesAndBoundsDiscoveredCalendars(int calendarCount, bool rejected)
    {
        var client = Substitute.For<ICalendarClient>();
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = "https://cal.example/000/"
        });
        var sut = new CalendarService(client, options, Substitute.For<ILogger<CalendarService>>());
        var calendars = Enumerable.Range(0, calendarCount)
            .Select(index => Calendar($"https://cal.example/{index:D3}/", $"Calendar {index:D3}", EntityKindSupport.Advertised))
            .Append(Calendar("https://cal.example/000/", "Duplicate", EntityKindSupport.Advertised))
            .ToArray();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(calendars);

        if (rejected)
        {
            var exception = await Should.ThrowAsync<CalendarDiscoveryLimitException>(() =>
                sut.GetCalendarsAsync(CancellationToken.None));
            exception.CalendarCount.ShouldBe(257);
        }
        else
        {
            var result = await sut.GetCalendarsAsync(CancellationToken.None);
            result.Items.ShouldHaveSingleItem().Href.ShouldBe("https://cal.example/000/");
        }
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

    private static byte[] TodoWithTemporalLines(string uid, string temporalLines) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\n{temporalLines}END:VTODO\r\nEND:VCALENDAR\r\n");

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

    private static byte[] OverrideEvent() => System.Text.Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:overrides\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260814T100000Z\r\nDURATION:PT1H\r\n"
        + "RRULE:FREQ=DAILY;COUNT=4\r\nEXDATE:20260815T100000Z\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:overrides\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260815T100000Z\r\n"
        + "DTSTART:20260815T120000Z\r\nDTEND:20260815T130000Z\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:overrides\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260816T100000Z\r\n"
        + "DTSTART:20260816T100000Z\r\nDTEND:20260816T110000Z\r\nSTATUS:CANCELLED\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:overrides\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260817T100000Z\r\n"
        + "DTSTART:20260817T200000Z\r\nDTEND:20260817T210000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] RangeOverrideEvent() => System.Text.Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:ranges\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260814T090000Z\r\nDURATION:PT1H\r\n"
        + "RRULE:FREQ=DAILY;COUNT=5\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:ranges\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260815T090000Z\r\n"
        + "DTSTART:20260815T110000Z\r\nDTEND:20260815T120000Z\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:ranges\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID;RANGE=THISANDFUTURE:20260817T090000Z\r\n"
        + "DTSTART:20260817T130000Z\r\nDTEND:20260817T150000Z\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:ranges\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260818T090000Z\r\n"
        + "DTSTART:20260818T160000Z\r\nDTEND:20260818T170000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] SameEffectiveStartRecurrence(string uid) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260815T100000Z\r\n"
        + "DURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260815T100000Z\r\n"
        + "DTSTART:20260820T100000Z\r\nDURATION:PT1M\r\nEND:VEVENT\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260816T100000Z\r\n"
        + "DTSTART:20260820T100000Z\r\nDURATION:PT1M\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] ResourceLocalZoneEvent() => System.Text.Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
        + "BEGIN:VTIMEZONE\r\nTZID:America/New_York\r\nBEGIN:STANDARD\r\n"
        + "DTSTART:20200101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
        + "END:STANDARD\r\nEND:VTIMEZONE\r\n"
        + "BEGIN:VEVENT\r\nUID:local-zone\r\nDTSTAMP:20260815T120000Z\r\n"
        + "DTSTART;TZID=America/New_York:20260816T100000\r\nDURATION:PT30M\r\n"
        + "END:VEVENT\r\nEND:VCALENDAR\r\n");

    private static string ResourceLocalDstZone() =>
        "BEGIN:VTIMEZONE\r\nTZID:Private/Zurich\r\n"
        + "BEGIN:DAYLIGHT\r\nDTSTART:20260329T020000\r\n"
        + "TZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\nEND:DAYLIGHT\r\n"
        + "BEGIN:STANDARD\r\nDTSTART:20261025T030000\r\n"
        + "TZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nEND:STANDARD\r\n"
        + "END:VTIMEZONE\r\n";

    private static string ResourceLocalFixedZone() =>
        "BEGIN:VTIMEZONE\r\nTZID:Private/Zurich\r\nBEGIN:STANDARD\r\n"
        + "DTSTART:19900101T000000\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0200\r\n"
        + "END:STANDARD\r\nEND:VTIMEZONE\r\n";

    private static byte[] RecurringTodo(string uid) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260815T100000Z\r\nDUE:20260815T103000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

    private static byte[] RecurringEventWithRule(string uid, string recurrenceLines) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260815T120000Z\r\nDURATION:PT1S\r\n{recurrenceLines}END:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] EventWithDetachedOverrides(int overrideCount)
    {
        var content = new System.Text.StringBuilder(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:detached-limit\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART:20200101T000000Z\r\nDURATION:PT1S\r\nRRULE:FREQ=DAILY;COUNT=1\r\n"
            + "EXDATE:20200101T000000Z\r\nEND:VEVENT\r\n");
        var identity = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        for (var index = 0; index < overrideCount; index++)
        {
            var value = identity.AddSeconds(index);
            content.Append("BEGIN:VEVENT\r\nUID:detached-limit\r\nDTSTAMP:20260815T120000Z\r\n")
                .Append($"RECURRENCE-ID:{value:yyyyMMdd'T'HHmmss'Z'}\r\n")
                .Append($"DTSTART:{value.AddYears(4):yyyyMMdd'T'HHmmss'Z'}\r\nDURATION:PT1S\r\nEND:VEVENT\r\n");
        }
        content.Append("END:VCALENDAR\r\n");
        return System.Text.Encoding.UTF8.GetBytes(content.ToString());
    }

    private static byte[] EventWithCompositeDerivedWork(int detachedCount)
    {
        var start = DateTimeOffset.Parse("2026-08-15T12:00:00Z");
        var periods = string.Join(',', Enumerable.Range(2000, 500).Select(index =>
            $"{start.AddSeconds(index):yyyyMMdd'T'HHmmss'Z'}/PT1S"));
        var content = new System.Text.StringBuilder(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:composite-limit\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART:20260815T120000Z\r\nDURATION:PT1S\r\nRRULE:FREQ=SECONDLY;COUNT=1000\r\n"
            + $"RDATE;VALUE=PERIOD:{periods}\r\nEND:VEVENT\r\n");
        for (var index = 0; index < detachedCount; index++)
        {
            var identity = start.AddSeconds(4000 + index);
            content.Append("BEGIN:VEVENT\r\nUID:composite-limit\r\nDTSTAMP:20260815T120000Z\r\n")
                .Append($"RECURRENCE-ID:{identity:yyyyMMdd'T'HHmmss'Z'}\r\n")
                .Append($"DTSTART:{identity.AddYears(1):yyyyMMdd'T'HHmmss'Z'}\r\nDURATION:PT1S\r\nEND:VEVENT\r\n");
        }
        content.Append("END:VCALENDAR\r\n");
        return System.Text.Encoding.UTF8.GetBytes(content.ToString());
    }

    private static byte[] Mixed(string eventUid, string todoUid) => System.Text.Encoding.UTF8.GetBytes(
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:{eventUid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260816T090000Z\r\nEND:VEVENT\r\nBEGIN:VTODO\r\nUID:{todoUid}\r\nDTSTAMP:20260815T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

    private static async Task<T> Delayed<T>(T result)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2.5));
        _ = await timer.WaitForNextTickAsync(TestContext.Current.CancellationToken);
        return result;
    }

    private static string CandidateResponse(IReadOnlyList<string> hrefs) =>
        "<d:multistatus xmlns:d=\"DAV:\">"
        + string.Concat(hrefs.Select(href =>
            $"<d:response><d:href>{href}</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response>"))
        + "</d:multistatus>";

    private static string MultigetResponse(IReadOnlyList<string> hrefs) =>
        "<d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
        + string.Concat(hrefs.Select((href, index) =>
        {
            var content = System.Text.Encoding.UTF8.GetString(
                Event($"event-{index + 1}", $"20260816T10{index:D2}00Z", $"Event {index + 1}"));
            return $"<d:response><d:href>{href}</d:href><d:propstat><d:prop>"
                + $"<d:getetag>&quot;r{index + 1}&quot;</d:getetag>"
                + $"<c:calendar-data>{System.Security.SecurityElement.Escape(content)}</c:calendar-data>"
                + "</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
        }))
        + "</d:multistatus>";

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

    private static async Task<CalendarOccurrenceQueryResult> QuerySingleOccurrenceEventAsync(
        string temporalLines,
        string from,
        string to,
        string? evaluationTimeZone = null,
        string calendarLines = "")
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/occurrence.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\n" + calendarLines
            + $"BEGIN:VEVENT\r\nUID:occurrence\r\nDTSTAMP:20260815T120000Z\r\n{temporalLines}END:VEVENT\r\nEND:VCALENDAR\r\n");
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>())
            .Returns(CalendarResourceRead.Success(resourceHref, "\"r1\"", bytes));
        return await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse(from),
                DateTimeOffset.Parse(to),
                evaluationTimeZone),
            CancellationToken.None);
    }

    private static async Task<CalendarOccurrenceQueryResult> QuerySingleOccurrenceAsync(
        byte[] authoritativeBytes,
        CalendarEntityKind kind,
        string from,
        string to,
        string? evaluationTimeZone = null)
    {
        const string calendarHref = "https://cal.example/calendar/";
        const string resourceHref = "https://cal.example/calendar/occurrence.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            EntityCalendar(
                calendarHref,
                "Calendar",
                kind == CalendarEntityKind.Event ? EntityKindSupport.Advertised : EntityKindSupport.NotAdvertised,
                kind == CalendarEntityKind.Todo ? EntityKindSupport.Advertised : EntityKindSupport.NotAdvertised)
        ]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                kind,
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                kind == CalendarEntityKind.Event ? CalendarEntityKind.Todo : CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", authoritativeBytes));
        return await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse(from),
                DateTimeOffset.Parse(to),
                evaluationTimeZone),
            CancellationToken.None);
    }

    private static async Task<CalendarTodoQueryResult> QuerySingleTodoAsync(
        byte[] authoritativeBytes,
        CalendarTodoQuery query)
    {
        const string calendarHref = "https://cal.example/todos/";
        const string resourceHref = "https://cal.example/todos/occurrence.ics";
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            EntityCalendar(calendarHref, "To-dos", EntityKindSupport.NotAdvertised, EntityKindSupport.Advertised)
        ]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns([resourceHref]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", authoritativeBytes));
        return await sut.QueryTodosAsync(query, CancellationToken.None);
    }

    private static async Task<CalendarOccurrenceQueryResult> QueryOccurrenceCountsAsync(params int[] occurrenceCounts)
    {
        const string calendarHref = "https://cal.example/events/";
        var hrefs = occurrenceCounts.Select((_, index) => $"{calendarHref}{index}.ics").ToArray();
        var client = Substitute.For<ICalendarClient>();
        var sut = new CalendarService(
            client,
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example" }),
            Substitute.For<ILogger<CalendarService>>());
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(
            [EntityCalendar(calendarHref, "Events", EntityKindSupport.Advertised, EntityKindSupport.NotAdvertised)]);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(hrefs);
        client.QueryCalendarResourceHrefsAsync(
                calendarHref,
                CalendarEntityKind.Todo,
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        for (var index = 0; index < hrefs.Length; index++)
        {
            var count = occurrenceCounts[index];
            client.GetCalendarResourceAsync(hrefs[index], Arg.Any<CancellationToken>()).Returns(
                CalendarResourceRead.Success(
                    hrefs[index],
                    $"\"r{index}\"",
                    RecurringEventWithRule($"occurrence-{index}", $"RRULE:FREQ=SECONDLY;COUNT={count}\r\n")));
        }
        return await sut.QueryOccurrencesAsync(
            new CalendarOccurrenceQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                DateTimeOffset.Parse("2026-08-15T12:00:00Z"),
                DateTimeOffset.Parse("2026-08-15T14:00:00Z")),
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

    private static CalendarService Service(
        ICalendarClient client,
        IOptions<CalDavOptions>? options = null) => new(
            client,
            options ?? Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                Username = "principal"
            }),
            Substitute.For<ILogger<CalendarService>>());

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

        public Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
            CalendarResourceCreateRequest request,
            CancellationToken cancellationToken) => transport.CreateCalendarResourceAsync(request, cancellationToken);

        public Task<CalendarResourceDeleteDispatchResult> DeleteCalendarResourceAsync(
            CalendarResourceDeleteRequest request,
            CancellationToken cancellationToken) => transport.DeleteCalendarResourceAsync(request, cancellationToken);
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

        public Task<IReadOnlyList<CalendarResourceRead>> GetCalendarResourcesForQueryAsync(
            string calendarHref,
            IReadOnlyList<string> hrefs,
            CancellationToken cancellationToken) => transport.GetCalendarResourcesForQueryAsync(
                calendarHref,
                hrefs,
                cancellationToken);

        public Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
            CalendarResourceCreateRequest request,
            CancellationToken cancellationToken) => transport.CreateCalendarResourceAsync(request, cancellationToken);

        public Task<CalendarResourceDeleteDispatchResult> DeleteCalendarResourceAsync(
            CalendarResourceDeleteRequest request,
            CancellationToken cancellationToken) => transport.DeleteCalendarResourceAsync(request, cancellationToken);
    }

    private sealed class RedirectHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }

    private sealed class AsyncHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
