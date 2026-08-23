using System.Diagnostics;
using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarQueryModuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ContinueReadsTheImmutableResultWithoutRepeatingCalDavWork()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var resourceHrefs = new[] { calendarHref + "a.ics", calendarHref + "b.ics" };
        var transport = new ScriptedCalendarQueryTransport([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Work",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            }
        ], () => resourceHrefs);
        var time = new MutableTimeProvider(Now);
        await using var provider = CreateProvider(transport, time);
        var module = provider.GetRequiredService<ICalendarQueryModule>();
        var query = new CalendarEntityQuery(CalendarEntityScope.All, [CalendarEntityKind.Event]);

        var first = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(query, PageSize: 1),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var cursor = first.Value.NextCursor.ShouldNotBeNull();
        resourceHrefs = [calendarHref + "changed.ics"];

        var continued = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(cursor, PageSize: 1),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        continued.Value.Items.ShouldHaveSingleItem().Value
            .GetProperty("resourceRevision").GetProperty("href").GetString()
            .ShouldBe(calendarHref + "b.ics");
        transport.DiscoveryCount.ShouldBe(1);
        transport.CandidateQueryCount.ShouldBe(1);
        transport.MultigetCount.ShouldBe(1);

        var fresh = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(query, PageSize: 50),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        fresh.Value.Items.ShouldHaveSingleItem().Value
            .GetProperty("resourceRevision").GetProperty("href").GetString()
            .ShouldBe(calendarHref + "changed.ics");
        transport.DiscoveryCount.ShouldBe(2);
    }

    [Fact]
    public async Task SnapshotLifetimeStartsWhenTheFirstPageIsBuilt()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var time = new MutableTimeProvider(Now);
        var transport = new ScriptedCalendarQueryTransport(
            [Calendar(calendarHref)],
            () => [calendarHref + "a.ics", calendarHref + "b.ics"],
            beforeCandidateReply: () => time.Advance(TimeSpan.FromSeconds(20)));
        await using var provider = CreateProvider(transport, time);
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var first = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query(), PageSize: 1),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var cursor = first.Value.NextCursor.ShouldNotBeNull();
        time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(45));

        (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(cursor),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        time.Advance(TimeSpan.FromSeconds(15));
        var expired = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(cursor),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();
        expired.Error.Code.ShouldBe(QueryFailureCode.CursorExpired);
    }

    [Fact]
    public async Task ElapsedTimeBudgetReturnsTypedLimitEvenWhenTransportDoesNotObserveCancellation()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var time = new MutableTimeProvider(Now);
        var transport = new ScriptedCalendarQueryTransport(
            [Calendar(calendarHref)],
            () => [calendarHref + "a.ics"],
            beforeCandidateReply: () => time.AdvanceWithoutTimers(TimeSpan.FromSeconds(30)));
        await using var provider = CreateProvider(transport, time);

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query()),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        failure.Error.Message.ShouldContain("elapsed_time");
        provider.GetRequiredService<CalendarQuerySnapshotStore>().ActiveReservationCount.ShouldBe(0);
    }

    [Fact]
    public async Task CursorReplayVariablePagesAndAuthenticationAreDeterministic()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var hrefs = Enumerable.Range(1, 3).Select(index => $"{calendarHref}{index}.ics").ToArray();
        var client = QueryClient(calendarHref, () => hrefs);
        var time = new MutableTimeProvider(Now);
        await using var provider = CreateProvider(client, time);
        var module = provider.GetRequiredService<ICalendarQueryModule>();
        var query = new CalendarEntityQuery(CalendarEntityScope.All, [CalendarEntityKind.Event]);
        var first = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(query, PageSize: 1),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var initialCursor = first.Value.NextCursor.ShouldNotBeNull();

        var one = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(initialCursor, PageSize: 1),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var oneReplay = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(initialCursor, PageSize: 1),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        one.Value.StructuredContent.GetRawText().ShouldBe(oneReplay.Value.StructuredContent.GetRawText());
        one.Value.NextCursor.ShouldBe(oneReplay.Value.NextCursor);

        var two = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(initialCursor, PageSize: 2),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var twoReplay = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(initialCursor, PageSize: 2),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        two.Value.Items.Count.ShouldBe(2);
        two.Value.NextCursor.ShouldBeNull();
        two.Value.StructuredContent.GetRawText().ShouldBe(twoReplay.Value.StructuredContent.GetRawText());

        var tampered = initialCursor[..^1] + (initialCursor[^1] == 'A' ? 'B' : 'A');
        var tamperFailure = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(tampered),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();
        tamperFailure.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);
        var wrongToolCursor = provider.GetRequiredService<CalendarQueryCursorIssuer>()
            .Issue("todos.query", Guid.NewGuid(), 1, Now.AddMinutes(10));
        var wrongToolFailure = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(wrongToolCursor),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();
        wrongToolFailure.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);

        await using var restarted = CreateProvider(client, time);
        var restartFailure = (await restarted.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(initialCursor),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();
        restartFailure.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);

        time.Advance(TimeSpan.FromMinutes(10));
        var expired = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(initialCursor),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();
        expired.Error.Code.ShouldBe(QueryFailureCode.CursorExpired);
        provider.GetRequiredService<CalendarQuerySnapshotStore>().ActiveSnapshotCount.ShouldBe(0);
        provider.GetRequiredService<CalendarQuerySnapshotStore>().RetainedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task EmptyStartReturnsACompleteUnretainedPage()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var client = QueryClient(calendarHref, static () => []);
        await using var provider = CreateProvider(client, new MutableTimeProvider(Now));

        var reply = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(
                new CalendarEntityQuery(CalendarEntityScope.All, [CalendarEntityKind.Event])),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        reply.Value.Items.ShouldBeEmpty();
        reply.Value.NextCursor.ShouldBeNull();
        reply.Value.StructuredContent.GetProperty("items").GetArrayLength().ShouldBe(0);
        provider.GetRequiredService<CalendarQuerySnapshotStore>().ActiveSnapshotCount.ShouldBe(0);
        provider.GetRequiredService<CalendarQuerySnapshotStore>().RetainedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task NullPublicRequestMembersReturnTypedInvalidInput()
    {
        await using var provider = CreateProvider(
            new ScriptedCalendarQueryTransport([Calendar("https://cal.example/calendars/work/")], static () => []),
            new MutableTimeProvider(Now));
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var start = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(null!),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();
        var continuation = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Continue(null!),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        start.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);
        continuation.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(200)]
    public async Task QueryTelemetryReportsActualWorkAndContinuationOnlyReadsSnapshot(int pageSize)
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var hrefs = Enumerable.Range(0, pageSize + 1).Select(index => $"{calendarHref}{index:D3}.ics").ToArray();
        var stopped = new List<Activity>();
        using var listener = ListenToQuery(stopped);
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        await using var provider = CreateProvider(
            new ScriptedCalendarQueryTransport([Calendar(calendarHref)], () => hrefs),
            new MutableTimeProvider(Now));
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        QueryReply<CalendarEntityQueryItem>.Page first;
        using (source.StartActivity("caldav.operation", ActivityKind.Internal))
        {
            first = (await module.QueryEntitiesAsync(
                new CalendarEntityQueryRequest.Start(Query(), pageSize),
                CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        }
        first.Value.Items.Count.ShouldBe(pageSize);
        var cursor = first.Value.NextCursor.ShouldNotBeNull();
        using (source.StartActivity("caldav.operation", ActivityKind.Internal))
        {
            (await module.QueryEntitiesAsync(
                new CalendarEntityQueryRequest.Continue(cursor, 200),
                CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        }

        var operations = stopped.Where(activity => activity.OperationName == "caldav.operation").ToArray();
        var start = operations.Single(activity => Equals(activity.GetTagItem("caldav.query.mode"), "start"));
        start.GetTagItem("caldav.query.fetch_mode").ShouldBe("multiget");
        Counter(start, "candidate_count").ShouldBe(pageSize + 1);
        Counter(start, "multiget_resource_count").ShouldBe(pageSize + 1);
        Counter(start, "snapshot_count").ShouldBe(pageSize + 1);
        Counter(start, "evaluation_count").ShouldBe(pageSize + 1);
        Counter(start, "serialization_count").ShouldBe(pageSize + 1);
        start.GetTagItem("caldav.query.snapshot_lookup_count").ShouldBeNull();
        Counter(start, "page_admission_count").ShouldBe(1);

        var continuation = operations.Single(activity => Equals(activity.GetTagItem("caldav.query.mode"), "continue"));
        continuation.GetTagItem("caldav.query.fetch_mode").ShouldBeNull();
        continuation.GetTagItem("caldav.query.candidate_count").ShouldBeNull();
        continuation.GetTagItem("caldav.query.multiget_resource_count").ShouldBeNull();
        continuation.GetTagItem("caldav.query.snapshot_count").ShouldBeNull();
        continuation.GetTagItem("caldav.query.evaluation_count").ShouldBeNull();
        continuation.GetTagItem("caldav.query.serialization_count").ShouldBeNull();
        Counter(continuation, "snapshot_lookup_count").ShouldBe(1);
        Counter(continuation, "page_admission_count").ShouldBe(1);
        stopped.Where(activity => activity.ParentId == start.Id && activity.OperationName.StartsWith("caldav.query.phase.", StringComparison.Ordinal))
            .Select(activity => activity.GetTagItem("caldav.query.phase"))
            .ShouldBe(["discovery", "candidate", "fetch", "evaluation", "serialization", "page_admission", "reservation"]);
        stopped.Where(activity => activity.ParentId == continuation.Id && activity.OperationName.StartsWith("caldav.query.phase.", StringComparison.Ordinal))
            .Select(activity => activity.GetTagItem("caldav.query.phase"))
            .ShouldBe(["snapshot_lookup", "page_admission"]);
    }

    [Fact]
    public async Task DefaultSelectionUsesOneScopedCoordinatorAcquisition()
    {
        const string allowedHref = "https://cal.example/calendars/allowed/";
        const string privateHref = "https://cal.example/calendars/private/";
        var client = QueryClient(allowedHref, static () => []);
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            Calendar(allowedHref),
            Calendar(privateHref) with { DisplayName = "Private" }
        ]);
        await using var provider = CreateProvider(client, new MutableTimeProvider(Now), options =>
        {
            options.CalendarHrefs = allowedHref;
            options.DefaultEventCalendarName = "Private";
        });

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(
                new CalendarEntityQuery(CalendarEntityScope.Default, [CalendarEntityKind.Event])),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.OutsideScope);
        failure.Error.AuthorizedCandidates.ShouldHaveSingleItem().CalendarHref.ShouldBe(allowedHref);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().QueryCalendarResourceHrefsAsync(
            Arg.Any<string>(),
            Arg.Any<CalendarEntityKind>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepeatedStartsOnOneModuleAcquireFreshProductionDiscovery()
    {
        const string firstCalendar = "https://cal.example/calendars/first/";
        const string secondCalendar = "https://cal.example/calendars/second/";
        var current = firstCalendar;
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(_ => [Calendar(current)]);
        client.QueryCalendarResourceHrefsAsync(
                Arg.Any<string>(),
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns(call => new[] { call.ArgAt<string>(0) + "item.ics" });
        client.GetCalendarResourcesForQueryAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<string>>(1)
                .Select(href => CalendarResourceRead.Success(href, "\"r1\"", Event(href))).ToArray());
        await using var provider = CreateProvider(client, new MutableTimeProvider(Now));
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var first = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query()),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        current = secondCalendar;
        var second = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query()),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        Href(first).ShouldBe(firstCalendar + "item.ics");
        Href(second).ShouldBe(secondCalendar + "item.ics");
        await client.Received(2).GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://user@cal.example/calendars/work/", true)]
    [InlineData("https://cal.example/calendars/work", true)]
    [InlineData("https://cal.example/calendars/work/%2e%2e/private.ics", false)]
    [InlineData("https://other.example/calendars/work/private.ics", false)]
    public async Task ExternalPortCannotEscapeCanonicalCalendarOrCandidateAuthority(string href, bool calendar)
    {
        const string safeCalendar = "https://cal.example/calendars/work/";
        var transport = calendar
            ? new ScriptedCalendarQueryTransport([Calendar(href)], static () => [])
            : new ScriptedCalendarQueryTransport([Calendar(safeCalendar)], () => [href]);
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query()),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.UpstreamProtocolError);
        transport.MultigetCount.ShouldBe(0);
    }

    [Fact]
    public async Task MultigetWorkCountsRequestedSlotsEvenWhenResponseIsIncomplete()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var stopped = new List<Activity>();
        using var listener = ListenToQuery(stopped);
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        var transport = new ScriptedCalendarQueryTransport(
            [Calendar(calendarHref)],
            () => [calendarHref + "a.ics", calendarHref + "b.ics"],
            reads: static _ => []);
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        using (source.StartActivity("caldav.operation"))
        {
            var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
                new CalendarEntityQueryRequest.Start(Query()),
                CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();
            failure.Error.Code.ShouldBe(QueryFailureCode.UpstreamProtocolError);
        }

        var operation = stopped.Single(activity => activity.OperationName == "caldav.operation");
        Counter(operation, "multiget_resource_count").ShouldBe(2);
        operation.GetTagItem("caldav.query.fetch_mode").ShouldBe("multiget");
    }

    [Fact]
    public async Task CallerCancellationAfterReservationPublishesAndRetainsNothing()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        using var cancellation = new CancellationTokenSource();
        var work = new CalendarQueryPageWorkCounter(cancellation.Cancel);
        await using var provider = CreateProvider(
            new ScriptedCalendarQueryTransport(
                [Calendar(calendarHref)],
                () => [calendarHref + "a.ics", calendarHref + "b.ics"]),
            new MutableTimeProvider(Now),
            services => services.AddTransient(serviceProvider => new CalendarEntityQueryPageCodec(
                serviceProvider.GetRequiredService<CalendarQueryCursorIssuer>(),
                work)));

        await Should.ThrowAsync<OperationCanceledException>(() => provider.GetRequiredService<ICalendarQueryModule>()
            .QueryEntitiesAsync(new CalendarEntityQueryRequest.Start(Query(), PageSize: 1), cancellation.Token));

        work.FinalMaterializationCount.ShouldBe(1);
        var store = provider.GetRequiredService<CalendarQuerySnapshotStore>();
        store.ActiveSnapshotCount.ShouldBe(0);
        store.ActiveReservationCount.ShouldBe(0);
        store.RetainedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task SnapshotPublicationFailureIsTypedAndRollsBackEveryStoreCounter()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        await using var provider = CreateProvider(
            new ScriptedCalendarQueryTransport(
                [Calendar(calendarHref)],
                () => [calendarHref + "a.ics", calendarHref + "b.ics"]),
            new SecondTimerThrowsTimeProvider());

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query(), PageSize: 1),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.UpstreamUnavailable);
        var store = provider.GetRequiredService<CalendarQuerySnapshotStore>();
        store.ActiveSnapshotCount.ShouldBe(0);
        store.ActiveReservationCount.ShouldBe(0);
        store.RetainedBytes.ShouldBe(0);
    }

    [Theory]
    [InlineData(CalendarEntityKind.Event, CalendarEntityKind.Todo)]
    [InlineData(CalendarEntityKind.Todo, CalendarEntityKind.Event)]
    public async Task CanonicalCalendarTraversalMakesFailurePrecedenceIndependentOfKindOrder(
        CalendarEntityKind first,
        CalendarEntityKind second)
    {
        var transport = new CanonicalFailureTransport();
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));
        var query = new CalendarEntityQuery(CalendarEntityScope.All, [first, second]);

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(query),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.PayloadTooLarge);
        transport.CandidateCalls.ShouldBe([
            (CanonicalFailureTransport.FirstCalendar, CalendarEntityKind.Todo),
            (CanonicalFailureTransport.SecondCalendar, CalendarEntityKind.Event)
        ]);
        transport.MultigetCalls.ShouldBe([CanonicalFailureTransport.FirstCalendar]);
    }

    private static ServiceProvider CreateProvider(
        ICalendarClient client,
        TimeProvider timeProvider,
        Action<CalDavOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
            configure?.Invoke(options);
        });
        services.AddSingleton(client);
        services.AddSingleton(timeProvider);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateProvider(
        ICalendarQueryTransport transport,
        TimeProvider timeProvider,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
        });
        services.AddSingleton(Substitute.For<ICalendarClient>());
        services.AddSingleton(timeProvider);
        services.AddSingleton(transport);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static ICalendarClient QueryClient(string calendarHref, Func<IReadOnlyList<string>> hrefs)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Work",
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
            .Returns(_ => hrefs());
        client.GetCalendarResourcesForQueryAsync(
                calendarHref,
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<string>>(1)
                .Select(href => CalendarResourceRead.Success(href, "\"r1\"", Event(href)))
                .ToArray());
        return client;
    }

    private static ReadOnlyMemory<byte> Event(string href) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Query Module Tests//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{Uri.EscapeDataString(href)}\r\nDTSTAMP:20260823T120000Z\r\n"
        + "DTSTART:20260824T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static CalendarEntityQuery Query() =>
        new(CalendarEntityScope.All, [CalendarEntityKind.Event]);

    private static CalendarDescriptor Calendar(string href) => new()
    {
        Href = href,
        DisplayName = "Work",
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.Advertised,
        TodoSupport = EntityKindSupport.NotAdvertised
    };

    private static ActivityListener ListenToQuery(ICollection<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarQueryTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static long Counter(Activity activity, string suffix) =>
        (long)activity.GetTagItem($"caldav.query.{suffix}")!;

    private static string? Href(QueryReply<CalendarEntityQueryItem>.Page reply) => reply.Value.Items
        .ShouldHaveSingleItem().Value.GetProperty("resourceRevision").GetProperty("href").GetString();

    private sealed class ScriptedCalendarQueryTransport(
        IReadOnlyList<CalendarDescriptor> calendars,
        Func<IReadOnlyList<string>> hrefs,
        Action? beforeCandidateReply = null,
        Func<IReadOnlyList<string>, IReadOnlyList<CalendarResourceRead>>? reads = null) : ICalendarQueryTransport
    {
        internal int DiscoveryCount { get; private set; }

        internal int CandidateQueryCount { get; private set; }

        internal int MultigetCount { get; private set; }

        public Task<CalendarQueryDiscovery> DiscoverAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscoveryCount++;
            var scoped = new CalendarDiscoveryResult(calendars, []);
            var eventDefault = CalendarSelectionResult.Success(calendars[0]);
            var todoDefault = CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, calendars);
            return Task.FromResult(new CalendarQueryDiscovery(scoped, eventDefault, todoDefault));
        }

        public Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
            string calendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CandidateQueryCount++;
            beforeCandidateReply?.Invoke();
            return Task.FromResult(hrefs());
        }

        public Task<IReadOnlyList<CalendarResourceRead>> MultigetAsync(
            string calendarHref,
            IReadOnlyList<string> resourceHrefs,
            CancellationToken cancellationToken)
        {
            MultigetCount++;
            return Task.FromResult(reads?.Invoke(resourceHrefs) ?? resourceHrefs
                .Select(href => CalendarResourceRead.Success(href, "\"r1\"", Event(href)))
                .ToArray());
        }

        public Task<CalendarResourceRead> GetAsync(
            string calendarHref,
            string resourceHref,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "Direct GET is forbidden until the strict multiget fallback decision is activated.");
    }

    private sealed class CanonicalFailureTransport : ICalendarQueryTransport
    {
        internal const string FirstCalendar = "https://cal.example/calendars/a/";
        internal const string SecondCalendar = "https://cal.example/calendars/z/";

        internal List<(string Href, CalendarEntityKind Kind)> CandidateCalls { get; } = [];

        internal List<string> MultigetCalls { get; } = [];

        public Task<CalendarQueryDiscovery> DiscoverAsync(CancellationToken cancellationToken)
        {
            var calendars = new[]
            {
                Calendar(SecondCalendar) with
                {
                    EventSupport = EntityKindSupport.Advertised,
                    TodoSupport = EntityKindSupport.NotAdvertised
                },
                Calendar(FirstCalendar) with
                {
                    EventSupport = EntityKindSupport.NotAdvertised,
                    TodoSupport = EntityKindSupport.Advertised
                }
            };
            return Task.FromResult(new CalendarQueryDiscovery(
                new CalendarDiscoveryResult(calendars, []),
                CalendarSelectionResult.Success(calendars[0]),
                CalendarSelectionResult.Success(calendars[1])));
        }

        public Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
            string calendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken)
        {
            CandidateCalls.Add((calendarHref, entityKind));
            return Task.FromResult<IReadOnlyList<string>>([calendarHref + "item.ics"]);
        }

        public Task<IReadOnlyList<CalendarResourceRead>> MultigetAsync(
            string calendarHref,
            IReadOnlyList<string> resourceHrefs,
            CancellationToken cancellationToken)
        {
            MultigetCalls.Add(calendarHref);
            var code = calendarHref == FirstCalendar
                ? CalendarResourceReadCode.PayloadTooLarge
                : CalendarResourceReadCode.ConcurrencyUnavailable;
            return Task.FromResult<IReadOnlyList<CalendarResourceRead>>([
                new CalendarResourceRead(code, resourceHrefs[0], ObservedByteCount: 33 * 1024 * 1024)
            ]);
        }

        public Task<CalendarResourceRead> GetAsync(
            string calendarHref,
            string resourceHref,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "Direct GET is forbidden until issue #109.");
    }

    private sealed class SecondTimerThrowsTimeProvider : TimeProvider
    {
        private int _timerCount;

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => Interlocked.Increment(ref _timerCount) == 2
                ? throw new InvalidOperationException("scripted snapshot timer failure")
                : new NoOpTimer();
    }

    private sealed class NoOpTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => utcNow;

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

        internal void Advance(TimeSpan amount)
        {
            utcNow += amount;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        internal void AdvanceWithoutTimers(TimeSpan amount) => utcNow += amount;

        private sealed class ManualTimer(
            MutableTimeProvider owner,
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

            internal void FireIfDue()
            {
                if (_disposed || _dueAt is null || owner.GetUtcNow() < _dueAt)
                    return;
                _dueAt = period == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + period;
                callback(state);
            }
        }
    }
}
