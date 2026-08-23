using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
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
        failure.Error.Limits!.Dimension.ShouldBe(QueryLimitDimension.ElapsedTime);
        failure.Error.Limits.Observed.ShouldBe(30_000);
        failure.Error.Limits.Limit.ShouldBe(30_000);
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
    public async Task ContinueRejectsInvalidPageSizeAndAuthenticatedSnapshotMismatches()
    {
        var time = new MutableTimeProvider(Now);
        await using var provider = CreateProvider(
            new ScriptedCalendarQueryTransport([Calendar("https://cal.example/calendars/work/")], static () => []),
            time);
        var module = provider.GetRequiredService<ICalendarQueryModule>();
        var issuer = provider.GetRequiredService<CalendarQueryCursorIssuer>();
        var store = provider.GetRequiredService<CalendarQuerySnapshotStore>();
        var id = Guid.NewGuid();
        var expires = Now.AddMinutes(10);
        var item = new StoredCalendarEntityQueryItem("{}"u8.ToArray());
        using var lease = store.TryReserve(new CalendarQuerySnapshot(
            id,
            expires,
            [item, item],
            "[]"u8.ToArray(),
            6)).Lease!;
        lease.Commit().ShouldBeTrue();
        var cases = new[]
        {
            new CalendarEntityQueryRequest.Continue(issuer.Issue(
                CalendarEntityQueryPageCodec.ToolName, Guid.NewGuid(), 1, expires)),
            new CalendarEntityQueryRequest.Continue(issuer.Issue(
                CalendarEntityQueryPageCodec.ToolName, id, 1, expires.AddSeconds(-1))),
            new CalendarEntityQueryRequest.Continue(issuer.Issue(
                CalendarEntityQueryPageCodec.ToolName, id, 2, expires)),
            new CalendarEntityQueryRequest.Continue(issuer.Issue(
                CalendarEntityQueryPageCodec.ToolName, id, 1, expires), 0),
            new CalendarEntityQueryRequest.Continue(issuer.Issue(
                CalendarEntityQueryPageCodec.ToolName, id, 1, expires), 201)
        };

        foreach (var request in cases)
        {
            var failure = (await module.QueryEntitiesAsync(request, CancellationToken.None))
                .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();
            failure.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);
        }
    }

    [Fact]
    public async Task ContinueHonorsExternalCancellationBeforeLookup()
    {
        await using var provider = CreateProvider(
            new ScriptedCalendarQueryTransport([Calendar("https://cal.example/calendars/work/")], static () => []),
            new MutableTimeProvider(Now));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => provider.GetRequiredService<ICalendarQueryModule>()
            .QueryEntitiesAsync(new CalendarEntityQueryRequest.Continue("opaque"), cancellation.Token));
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
        var client = Substitute.For<ICalendarClient, ICalendarQueryResourceTransport>();
        var queryTransport = (ICalendarQueryResourceTransport)client;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns(_ => [Calendar(current)]);
        client.QueryCalendarResourceHrefsAsync(
                Arg.Any<string>(),
                CalendarEntityKind.Event,
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns(call => new[] { call.ArgAt<string>(0) + "item.ics" });
        queryTransport.MultigetAsync(
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
    [InlineData("ftp://cal.example/calendars/work/", true)]
    [InlineData("https://cal.example/calendars/work/?private=1", true)]
    [InlineData("https://cal.example/calendars/work/#private", true)]
    [InlineData("https://cal.example/calendars/%2Fwork/", true)]
    [InlineData("https://cal.example/calendars/%2e%2e/work/", true)]
    [InlineData("https://cal.example/calendars/work/%2e%2e/private.ics", false)]
    [InlineData("https://other.example/calendars/work/private.ics", false)]
    [InlineData("https://user@cal.example/calendars/work/private.ics", false)]
    [InlineData("https://cal.example/calendars/work/private.ics?secret=1", false)]
    [InlineData("https://cal.example/calendars/work/private.ics#secret", false)]
    [InlineData("https://cal.example/calendars/work/nested/private.ics", false)]
    [InlineData("https://cal.example/calendars/work/", false)]
    [InlineData("https://cal.example/calendars/work/%2Fprivate.ics", false)]
    [InlineData("relative.ics", false)]
    [InlineData("ftp://cal.example/calendars/work/private.ics", false)]
    [InlineData("https://cal.example/calendars/sibling/private.ics", false)]
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

    [Theory]
    [MemberData(nameof(InvalidStartRequests))]
    public async Task InvalidStartShapesFailBeforeAnyTransportWork(CalendarEntityQueryRequest.Start request)
    {
        var transport = new ScriptedCalendarQueryTransport(
            [Calendar("https://cal.example/calendars/work/")],
            static () => []);
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            request,
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);
        transport.DiscoveryCount.ShouldBe(0);
    }

    public static IEnumerable<object[]> InvalidStartRequests()
    {
        var from = Now;
        var to = Now.AddDays(1);
        yield return [new CalendarEntityQueryRequest.Start(Query(), 0)];
        yield return [new CalendarEntityQueryRequest.Start(Query(), 201)];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(CalendarEntityScope.All, []))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
            CalendarEntityScope.All,
            [CalendarEntityKind.Event, CalendarEntityKind.Event]))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
            CalendarEntityScope.All,
            [(CalendarEntityKind)99]))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
            new CalendarEntityScope(CalendarEntityScopeMode.All, new CalendarReference(Name: "Work")),
            [CalendarEntityKind.Event]))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
            new CalendarEntityScope(CalendarEntityScopeMode.Default, new CalendarReference(Name: "Work")),
            [CalendarEntityKind.Event]))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
            new CalendarEntityScope(CalendarEntityScopeMode.Selected),
            [CalendarEntityKind.Event]))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
            CalendarEntityScope.Selected(new CalendarReference()),
            [CalendarEntityKind.Event]))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
            CalendarEntityScope.Selected(new CalendarReference("Work", "https://cal.example/calendars/work/")),
            [CalendarEntityKind.Event]))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
            CalendarEntityScope.Selected(new CalendarReference(Name: " Work ")),
            [CalendarEntityKind.Event]))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(CalendarEntityScope.All,
            [CalendarEntityKind.Event], from, null))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(CalendarEntityScope.All,
            [CalendarEntityKind.Event], from.ToOffset(TimeSpan.FromHours(1)), to))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(CalendarEntityScope.All,
            [CalendarEntityKind.Event], to, from))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(CalendarEntityScope.All,
            [CalendarEntityKind.Event], from, from.AddDays(367)))];
        yield return [new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
            new CalendarEntityScope((CalendarEntityScopeMode)99),
            [CalendarEntityKind.Event]))];
    }

    [Theory]
    [InlineData("limit", QueryFailureCode.LimitExhausted)]
    [InlineData("unauthorized", QueryFailureCode.UpstreamUnauthorized)]
    [InlineData("cancelled", QueryFailureCode.UpstreamUnavailable)]
    [InlineData("timeout", QueryFailureCode.UpstreamUnavailable)]
    [InlineData("xml", QueryFailureCode.UpstreamProtocolError)]
    [InlineData("protocol", QueryFailureCode.UpstreamProtocolError)]
    [InlineData("unsupported", QueryFailureCode.UnsupportedCapability)]
    public async Task DiscoveryExceptionsMapToClosedFailures(string scenario, QueryFailureCode expected)
    {
        Exception exception = scenario switch
        {
            "limit" => new CalendarDiscoveryLimitException(257),
            "unauthorized" => new HttpRequestException("private", null, HttpStatusCode.Unauthorized),
            "cancelled" => new OperationCanceledException(),
            "timeout" => new TimeoutException("private"),
            "xml" => new XmlException("private"),
            "protocol" => new CalendarDiscoveryProtocolException("private"),
            "unsupported" => new CalendarDiscoveryUnsupportedCapabilityException("private"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var transport = new DelegateTransport(discover: _ => Task.FromException<CalendarQueryDiscovery>(exception));
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query()),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(expected);
        if (scenario == "limit")
            failure.Error.Limits!.CalendarCount.ShouldBe(257);
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.NotFound, null)]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable, QueryFailureCode.ConcurrencyUnavailable)]
    [InlineData(CalendarResourceReadCode.PayloadTooLarge, QueryFailureCode.PayloadTooLarge)]
    [InlineData(CalendarResourceReadCode.UnsupportedCapability, QueryFailureCode.UnsupportedCapability)]
    [InlineData(CalendarResourceReadCode.InvalidInput, QueryFailureCode.UpstreamProtocolError)]
    [InlineData(CalendarResourceReadCode.OutsideScope, QueryFailureCode.UpstreamProtocolError)]
    [InlineData(CalendarResourceReadCode.UpstreamProtocolError, QueryFailureCode.UpstreamProtocolError)]
    public async Task AuthoritativeReadOutcomesAreAllOrNothing(
        CalendarResourceReadCode code,
        QueryFailureCode? expected)
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        const string resourceHref = calendarHref + "item.ics";
        var transport = new ScriptedCalendarQueryTransport(
            [Calendar(calendarHref)],
            static () => [resourceHref],
            reads: _ => [new CalendarResourceRead(code, resourceHref, ObservedByteCount: 33 * 1024 * 1024)]);
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var reply = await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query()),
            CancellationToken.None);

        if (expected is null)
        {
            var page = reply.ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
            page.Value.Items.ShouldBeEmpty();
            page.Value.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "resource_disappeared_during_query");
        }
        else
        {
            var failure = reply.ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();
            failure.Error.Code.ShouldBe(expected.Value);
            if (code == CalendarResourceReadCode.PayloadTooLarge)
                failure.Error.Limits!.ByteCount.ShouldBe(33 * 1024 * 1024);
        }
    }

    [Fact]
    public async Task MismatchedReadIdentityAndWeakRevisionFailClosed()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        const string resourceHref = calendarHref + "item.ics";
        var reads = new[]
        {
            new CalendarResourceRead(CalendarResourceReadCode.Success, calendarHref + "other.ics", "\"r1\"", Event(resourceHref)),
            new CalendarResourceRead(CalendarResourceReadCode.Success, resourceHref, "W/\"r1\"", Event(resourceHref)),
            new CalendarResourceRead(CalendarResourceReadCode.Success, resourceHref, null, Event(resourceHref))
        };
        foreach (var read in reads)
        {
            var transport = new ScriptedCalendarQueryTransport(
                [Calendar(calendarHref)],
                static () => [resourceHref],
                reads: _ => [read]);
            await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

            var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
                new CalendarEntityQueryRequest.Start(Query()),
                CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

            failure.Error.Code.ShouldBe(QueryFailureCode.UpstreamProtocolError);
        }
    }

    [Theory]
    [InlineData(CalendarSelectionCode.NotFound, QueryFailureCode.NotFound)]
    [InlineData(CalendarSelectionCode.Ambiguous, QueryFailureCode.Ambiguous)]
    [InlineData(CalendarSelectionCode.OutsideScope, QueryFailureCode.OutsideScope)]
    [InlineData(CalendarSelectionCode.UnsupportedCapability, QueryFailureCode.UnsupportedCapability)]
    public async Task DefaultSelectionFailuresRemainClosed(
        CalendarSelectionCode selectionCode,
        QueryFailureCode expected)
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var calendar = Calendar(calendarHref);
        var discovery = new CalendarQueryDiscovery(
            new CalendarDiscoveryResult([calendar], []),
            CalendarSelectionResult.Failure(selectionCode, [calendar]),
            CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, [calendar]));
        var transport = new DelegateTransport(discover: _ => Task.FromResult(discovery));
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.Default,
                [CalendarEntityKind.Event])),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(expected);
    }

    [Fact]
    public async Task DistinctEventAndTodoDefaultsAreBothSelectedInFixedKindOrder()
    {
        const string eventsHref = "https://cal.example/calendars/events/";
        const string todosHref = "https://cal.example/calendars/todos/";
        var events = Calendar(eventsHref);
        var todos = Calendar(todosHref) with
        {
            EventSupport = EntityKindSupport.NotAdvertised,
            TodoSupport = EntityKindSupport.Advertised
        };
        var discovery = new CalendarQueryDiscovery(
            new CalendarDiscoveryResult([todos, events], []),
            CalendarSelectionResult.Success(events),
            CalendarSelectionResult.Success(todos));
        var calls = new List<(string Href, CalendarEntityKind Kind)>();
        var transport = new DelegateTransport(
            discover: _ => Task.FromResult(discovery),
            candidates: (href, kind, _, _, _) =>
            {
                calls.Add((href, kind));
                return Task.FromResult<IReadOnlyList<string>>([]);
            });
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.Default,
                [CalendarEntityKind.Todo, CalendarEntityKind.Event])),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        calls.ShouldBe([(eventsHref, CalendarEntityKind.Event), (todosHref, CalendarEntityKind.Todo)]);
    }

    [Fact]
    public async Task SelectedSupportedCalendarReturnsNoKindMismatchDiagnostic()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var calendar = Calendar(calendarHref);
        var discovery = new CalendarQueryDiscovery(
            new CalendarDiscoveryResult([calendar], []),
            CalendarSelectionResult.Success(calendar),
            CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound));
        var transport = new DelegateTransport(discover: _ => Task.FromResult(discovery));
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                [CalendarEntityKind.Event])),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        page.Value.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task SelectedNameReportsNotFoundAmbiguousAndKindEvidenceWithoutBroadening()
    {
        const string firstHref = "https://cal.example/calendars/first/";
        const string secondHref = "https://cal.example/calendars/second/";
        var first = Calendar(firstHref) with { DisplayName = " Work ", EventSupport = EntityKindSupport.NotAdvertised };
        var second = Calendar(secondHref) with { DisplayName = "work", EventSupport = EntityKindSupport.NotAdvertised };
        var discovery = new CalendarQueryDiscovery(
            new CalendarDiscoveryResult([first, second], []),
            CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, [first, second]),
            CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, [first, second]));
        var transport = new DelegateTransport(discover: _ => Task.FromResult(discovery));
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var missing = (await module.QueryEntitiesAsync(new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
            CalendarEntityScope.Selected(new CalendarReference(Name: "Missing")),
            [CalendarEntityKind.Event])), CancellationToken.None))
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();
        var ambiguous = (await module.QueryEntitiesAsync(new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
            CalendarEntityScope.Selected(new CalendarReference(Name: "work")),
            [CalendarEntityKind.Event])), CancellationToken.None))
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        missing.Error.Code.ShouldBe(QueryFailureCode.NotFound);
        ambiguous.Error.Code.ShouldBe(QueryFailureCode.Ambiguous);
        missing.Error.AuthorizedCandidates!.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("relative/calendar/")]
    [InlineData("https://other.example/calendars/work/")]
    [InlineData("https://user@cal.example/calendars/work/")]
    [InlineData("https://cal.example/calendars/work/?secret=1")]
    [InlineData("https://cal.example/calendars/%2Fwork/")]
    public async Task SelectedHrefIsRejectedBeforeDiscoveryWhenItIsUnsafe(string href)
    {
        var transport = new ScriptedCalendarQueryTransport(
            [Calendar("https://cal.example/calendars/work/")],
            static () => []);
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: href)),
                [CalendarEntityKind.Event])),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);
        failure.Error.Phase.ShouldBe(QueryFailurePhase.OriginScopeAuthorization);
        transport.DiscoveryCount.ShouldBe(0);
    }

    [Fact]
    public async Task SelectedHrefOutsideConfiguredScopeDoesNotAcquireDiscovery()
    {
        const string allowed = "https://cal.example/calendars/allowed/";
        const string selected = "https://cal.example/calendars/selected/";
        var client = QueryClient(allowed, static () => []);
        await using var provider = CreateProvider(client, new MutableTimeProvider(Now), options =>
            options.CalendarHrefs = allowed);

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: selected)),
                [CalendarEntityKind.Event])),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.OutsideScope);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SafeDiscoveryDiagnosticsAndSelectedKindMismatchAreFrozenIntoThePage()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var calendar = Calendar(calendarHref) with { EventSupport = EntityKindSupport.NotAdvertised };
        var discovery = new CalendarQueryDiscovery(
            new CalendarDiscoveryResult([calendar],
            [
                new CalendarDiagnostic("duplicate_calendar_href", "private duplicate", CalendarDiagnosticSeverity.Info),
                new CalendarDiagnostic("calendar_href_not_found", "private missing", CalendarDiagnosticSeverity.Error)
            ]),
            CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, [calendar]),
            CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, [calendar]));
        var transport = new DelegateTransport(
            discover: _ => Task.FromResult(discovery),
            candidates: (_, _, _, _, _) => Task.FromResult<IReadOnlyList<string>>([]));
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref)),
                [CalendarEntityKind.Event])),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        page.Value.Diagnostics.Select(diagnostic => diagnostic.Code).ShouldBe([
            "duplicate_calendar_href", "calendar_href_not_found", "entity_kind_not_advertised"]);
        page.Value.Diagnostics.ShouldAllBe(diagnostic => !diagnostic.Message.Contains("private", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("too_many")]
    [InlineData("duplicate")]
    [InlineData("not_collection")]
    [InlineData("unknown_diagnostic")]
    [InlineData("escaped_default")]
    [InlineData("missing_success_default")]
    public async Task InvalidScopedDiscoveryTruthFailsBeforeCandidateIo(string scenario)
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var calendar = Calendar(calendarHref);
        IReadOnlyList<CalendarDescriptor> calendars = scenario switch
        {
            "too_many" => Enumerable.Range(0, 257)
                .Select(index => Calendar($"https://cal.example/calendars/{index:D3}/"))
                .ToArray(),
            "duplicate" => [calendar, calendar],
            "not_collection" => [Calendar("https://cal.example/calendars/work")],
            _ => [calendar]
        };
        IReadOnlyList<CalendarDiagnostic> diagnostics = scenario == "unknown_diagnostic"
            ? [new CalendarDiagnostic("private", "private", CalendarDiagnosticSeverity.Warning)]
            : [];
        var escaped = Calendar("https://cal.example/calendars/escaped/");
        var eventDefault = scenario switch
        {
            "escaped_default" => CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, [escaped]),
            "missing_success_default" => new CalendarSelectionResult(CalendarSelectionCode.Success, null, []),
            _ => CalendarSelectionResult.Success(calendars[0])
        };
        var discovery = new CalendarQueryDiscovery(
            new CalendarDiscoveryResult(calendars, diagnostics),
            eventDefault,
            CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, calendars));
        var candidateCalls = 0;
        var transport = new DelegateTransport(
            discover: _ => Task.FromResult(discovery),
            candidates: (_, _, _, _, _) =>
            {
                candidateCalls++;
                return Task.FromResult<IReadOnlyList<string>>([]);
            });
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query()),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.UpstreamProtocolError);
        candidateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task EventTodoAndOpaqueResourcesUseTheirClosedProjectionKinds()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var hrefs = new[] { calendarHref + "event.ics", calendarHref + "opaque.ics", calendarHref + "todo.ics" };
        var calendar = Calendar(calendarHref) with { TodoSupport = EntityKindSupport.Advertised };
        var transport = new ScriptedCalendarQueryTransport(
            [calendar],
            () => hrefs,
            reads: requested => requested.Select(href => CalendarResourceRead.Success(
                href,
                "\"r1\"",
                href.EndsWith("event.ics", StringComparison.Ordinal)
                    ? Event(href)
                    : href.EndsWith("todo.ics", StringComparison.Ordinal)
                        ? Todo(href)
                        : Mixed(href))).ToArray());
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.All,
                [CalendarEntityKind.Event, CalendarEntityKind.Todo])),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        page.Value.Items.Select(item => item.Value.GetProperty("projection").GetProperty("kind").GetString())
            .ShouldBe(["event", "opaque", "todo"]);
    }

    [Theory]
    [InlineData("no_match", null)]
    [InlineData("unresolved", QueryFailureCode.TemporalUnresolved)]
    [InlineData("unevaluable", QueryFailureCode.RecurrenceUnevaluable)]
    public async Task TemporalEvaluationReturnsClosedMatchOutcomes(string scenario, QueryFailureCode? expected)
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        const string resourceHref = calendarHref + "item.ics";
        var bytes = scenario switch
        {
            "no_match" => Event(resourceHref),
            "unresolved" => Ics("BEGIN:VEVENT\r\nUID:item\r\nDTSTAMP:20260823T120000Z\r\nDTSTART;TZID=Private/Unknown:20260824T120000\r\nEND:VEVENT\r\n"),
            "unevaluable" => Ics("BEGIN:VEVENT\r\nUID:item\r\nDTSTAMP:20260823T120000Z\r\nDTSTART:20260824T120000Z\r\nRRULE:FREQ=DAILY\r\nRRULE:FREQ=WEEKLY\r\nEND:VEVENT\r\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var transport = new ScriptedCalendarQueryTransport(
            [Calendar(calendarHref)],
            static () => [resourceHref],
            reads: _ => [CalendarResourceRead.Success(resourceHref, "\"r1\"", bytes)]);
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));
        var query = new CalendarEntityQuery(
            CalendarEntityScope.All,
            [CalendarEntityKind.Event],
            Now.AddDays(10),
            Now.AddDays(11));

        var reply = await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(query),
            CancellationToken.None);

        if (expected is null)
            reply.ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>().Value.Items.ShouldBeEmpty();
        else
            reply.ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error.Code.ShouldBe(expected.Value);
    }

    [Fact]
    public async Task CandidateLimitStopsBeforeAuthoritativeReadsWithoutPartialItems()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var hrefs = Enumerable.Range(0, CalendarEntityQueryStartExecutor.MaximumSnapshotItems + 1)
            .Select(index => $"{calendarHref}{index:D4}.ics")
            .ToArray();
        var transport = new ScriptedCalendarQueryTransport([Calendar(calendarHref)], () => hrefs);
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query()),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        failure.Error.Limits!.ResourcesInspected.ShouldBe(CalendarEntityQueryStartExecutor.MaximumSnapshotItems + 1);
        transport.MultigetCount.ShouldBe(0);
    }

    [Fact]
    public async Task DisappearanceDiagnosticsAreCappedAndContentSafe()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var hrefs = Enumerable.Range(0, 40).Select(index => $"{calendarHref}{index:D2}.ics").ToArray();
        var transport = new ScriptedCalendarQueryTransport(
            [Calendar(calendarHref)],
            () => hrefs,
            reads: requested => requested.Select(href => new CalendarResourceRead(
                CalendarResourceReadCode.NotFound,
                href)).ToArray());
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query()),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        page.Value.Items.ShouldBeEmpty();
        page.Value.Diagnostics.Count.ShouldBe(32);
        page.Value.Diagnostics.ShouldAllBe(diagnostic => diagnostic.Code == "resource_disappeared_during_query");
    }

    [Fact]
    public async Task SnapshotCapacityBusyFailureIncludesBoundedRetryAndPublishesNoExtraState()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var time = new MutableTimeProvider(Now);
        var transport = new ScriptedCalendarQueryTransport(
            [Calendar(calendarHref)],
            () => [calendarHref + "a.ics", calendarHref + "b.ics"]);
        await using var provider = CreateProvider(transport, time);
        var store = provider.GetRequiredService<CalendarQuerySnapshotStore>();
        for (var index = 0; index < CalendarQuerySnapshotStore.MaximumSnapshots; index++)
        {
            using var lease = store.TryReserve(new CalendarQuerySnapshot(
                Guid.NewGuid(),
                Now.AddMinutes(10),
                ImmutableArray<StoredCalendarEntityQueryItem>.Empty,
                ReadOnlyMemory<byte>.Empty,
                0)).Lease!;
            lease.Commit().ShouldBeTrue();
        }

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(Query(), PageSize: 1),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.Busy);
        failure.Error.RetryAfterMs.ShouldBe(600_000);
        store.ActiveSnapshotCount.ShouldBe(CalendarQuerySnapshotStore.MaximumSnapshots);
        store.ActiveReservationCount.ShouldBe(0);
    }

    [Fact]
    public async Task RecurrenceWorkLimitReturnsNoPartialPage()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        const string resourceHref = calendarHref + "recurring.ics";
        var bytes = Ics("BEGIN:VEVENT\r\nUID:recurring\r\nDTSTAMP:20260823T120000Z\r\n"
            + "DTSTART:20260823T120000Z\r\nRRULE:FREQ=SECONDLY;COUNT=5002\r\nEND:VEVENT\r\n");
        var transport = new ScriptedCalendarQueryTransport(
            [Calendar(calendarHref)],
            static () => [resourceHref],
            reads: _ => [CalendarResourceRead.Success(resourceHref, "\"r1\"", bytes)]);
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.All,
                [CalendarEntityKind.Event],
                Now,
                Now.AddHours(2))),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        failure.Error.Limits!.OccurrenceCount.ShouldBe(2001);
    }

    [Fact]
    public async Task TotalRecurrenceWorkLimitAccumulatesAcrossResourcesWithoutPartialPage()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var hrefs = Enumerable.Range(0, 3).Select(index => $"{calendarHref}{index}.ics").ToArray();
        var transport = new ScriptedCalendarQueryTransport(
            [Calendar(calendarHref)],
            () => hrefs,
            reads: requested => requested.Select(href => CalendarResourceRead.Success(
                href,
                "\"r1\"",
                Ics($"BEGIN:VEVENT\r\nUID:{Uri.EscapeDataString(href)}\r\nDTSTAMP:20260823T120000Z\r\n"
                    + "DTSTART:20260823T120000Z\r\nRRULE:FREQ=SECONDLY;COUNT=2000\r\nEND:VEVENT\r\n")))
                .ToArray());
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.All,
                [CalendarEntityKind.Event],
                Now,
                Now.AddHours(2))),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        failure.Error.Limits!.OccurrenceCount.ShouldBe(6000);
    }

    [Theory]
    [MemberData(nameof(TemporalSemanticCases))]
    public async Task TemporalSemanticVariantsAreEvaluatedFromAuthoritativeSnapshots(
        string component,
        CalendarEntityKind kind,
        int expectedItems)
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        const string resourceHref = calendarHref + "item.ics";
        var calendar = Calendar(calendarHref) with
        {
            EventSupport = kind == CalendarEntityKind.Event
                ? EntityKindSupport.Advertised
                : EntityKindSupport.NotAdvertised,
            TodoSupport = kind == CalendarEntityKind.Todo
                ? EntityKindSupport.Advertised
                : EntityKindSupport.NotAdvertised
        };
        var transport = new ScriptedCalendarQueryTransport(
            [calendar],
            static () => [resourceHref],
            reads: _ => [CalendarResourceRead.Success(resourceHref, "\"r1\"", Ics(component))]);
        await using var provider = CreateProvider(transport, new MutableTimeProvider(Now));

        var reply = await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.All,
                [kind],
                Now,
                Now.AddDays(4))),
            CancellationToken.None);

        if (expectedItems < 0)
            reply.ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error.Code
                .ShouldBe(QueryFailureCode.TemporalUnresolved);
        else
            reply.ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>().Value.Items.Count.ShouldBe(expectedItems);
    }

    public static IEnumerable<object[]> TemporalSemanticCases()
    {
        yield return [
            "BEGIN:VEVENT\r\nUID:event-start\r\nDTSTAMP:20260823T120000Z\r\n"
            + "DTSTART:20260823T120000Z\r\nDTEND:20260823T130000Z\r\nEND:VEVENT\r\n",
            CalendarEntityKind.Event,
            1];
        yield return [
            "BEGIN:VEVENT\r\nUID:event-before\r\nDTSTAMP:20260823T120000Z\r\n"
            + "DTSTART:20260823T100000Z\r\nDTEND:20260823T120000Z\r\nEND:VEVENT\r\n",
            CalendarEntityKind.Event,
            0];
        yield return [
            "BEGIN:VEVENT\r\nUID:event-duration\r\nDTSTAMP:20260823T120000Z\r\n"
            + "DTSTART:20260824T120000Z\r\nDURATION:PT2H\r\nEND:VEVENT\r\n",
            CalendarEntityKind.Event,
            1];
        yield return [
            "BEGIN:VEVENT\r\nUID:event-date\r\nDTSTAMP:20260823T120000Z\r\n"
            + "DTSTART;VALUE=DATE:20260824\r\nEND:VEVENT\r\n",
            CalendarEntityKind.Event,
            -1];
        yield return [
            "BEGIN:VEVENT\r\nUID:event-recur\r\nDTSTAMP:20260823T120000Z\r\n"
            + "DTSTART:20260823T120000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\n"
            + "EXDATE:20260824T120000Z\r\nEND:VEVENT\r\n",
            CalendarEntityKind.Event,
            1];
        yield return [
            "BEGIN:VEVENT\r\nUID:event-period\r\nDTSTAMP:20260823T120000Z\r\n"
            + "DTSTART:20260830T120000Z\r\nRDATE;VALUE=PERIOD:20260824T120000Z/PT1H\r\nEND:VEVENT\r\n",
            CalendarEntityKind.Event,
            1];
        yield return [
            "BEGIN:VTODO\r\nUID:todo-due\r\nDTSTAMP:20260823T120000Z\r\n"
            + "DUE:20260824T120000Z\r\nEND:VTODO\r\n",
            CalendarEntityKind.Todo,
            1];
        yield return [
            "BEGIN:VTODO\r\nUID:todo-span\r\nDTSTAMP:20260823T120000Z\r\n"
            + "DTSTART:20260824T120000Z\r\nDUE:20260825T120000Z\r\nCOMPLETED:20260825T130000Z\r\nEND:VTODO\r\n",
            CalendarEntityKind.Todo,
            1];
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
        var client = Substitute.For<ICalendarClient, ICalendarQueryResourceTransport>();
        var queryTransport = (ICalendarQueryResourceTransport)client;
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
        queryTransport.MultigetAsync(
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

    private static ReadOnlyMemory<byte> Todo(string href) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Query Module Tests//EN\r\n"
        + $"BEGIN:VTODO\r\nUID:{Uri.EscapeDataString(href)}\r\nDTSTAMP:20260823T120000Z\r\n"
        + "DTSTART:20260824T120000Z\r\nDUE:20260824T130000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

    private static ReadOnlyMemory<byte> Mixed(string href) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Query Module Tests//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{Uri.EscapeDataString(href)}-event\r\nDTSTAMP:20260823T120000Z\r\n"
        + "DTSTART:20260824T120000Z\r\nEND:VEVENT\r\n"
        + $"BEGIN:VTODO\r\nUID:{Uri.EscapeDataString(href)}-todo\r\nDTSTAMP:20260823T120000Z\r\n"
        + "DUE:20260824T130000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");

    private static ReadOnlyMemory<byte> Ics(string component) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Query Module Tests//EN\r\n"
        + component
        + "END:VCALENDAR\r\n");

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

        public Task<CalendarMultigetResult> MultigetAsync(
            string calendarHref,
            IReadOnlyList<string> resourceHrefs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MultigetCount++;
            CalendarQueryTelemetry.ObserveMultigetAttempt(resourceHrefs.Count);
            return Task.FromResult<CalendarMultigetResult>(new CalendarMultigetResult.Resources(
                reads?.Invoke(resourceHrefs) ?? resourceHrefs
                    .Select(href => CalendarResourceRead.Success(href, "\"r1\"", Event(href)))
                    .ToArray()));
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

        public Task<CalendarMultigetResult> MultigetAsync(
            string calendarHref,
            IReadOnlyList<string> resourceHrefs,
            CancellationToken cancellationToken)
        {
            MultigetCalls.Add(calendarHref);
            var code = calendarHref == FirstCalendar
                ? CalendarResourceReadCode.PayloadTooLarge
                : CalendarResourceReadCode.ConcurrencyUnavailable;
            return Task.FromResult<CalendarMultigetResult>(new CalendarMultigetResult.Resources([
                new CalendarResourceRead(code, resourceHrefs[0], ObservedByteCount: 33 * 1024 * 1024)
            ]));
        }

        public Task<CalendarResourceRead> GetAsync(
            string calendarHref,
            string resourceHref,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
            "Direct GET is forbidden until issue #109.");
    }

    private sealed class DelegateTransport(
        Func<CancellationToken, Task<CalendarQueryDiscovery>>? discover = null,
        Func<string, CalendarEntityKind, DateTimeOffset?, DateTimeOffset?, CancellationToken,
            Task<IReadOnlyList<string>>>? candidates = null,
        Func<string, IReadOnlyList<string>, CancellationToken,
            Task<IReadOnlyList<CalendarResourceRead>>>? multiget = null) : ICalendarQueryTransport
    {
        public Task<CalendarQueryDiscovery> DiscoverAsync(CancellationToken cancellationToken) =>
            discover?.Invoke(cancellationToken)
            ?? Task.FromResult(new CalendarQueryDiscovery(
                new CalendarDiscoveryResult([Calendar("https://cal.example/calendars/work/")], []),
                CalendarSelectionResult.Success(Calendar("https://cal.example/calendars/work/")),
                CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound)));

        public Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
            string calendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken) => candidates?.Invoke(calendarHref, entityKind, from, to, cancellationToken)
            ?? Task.FromResult<IReadOnlyList<string>>([]);

        public async Task<CalendarMultigetResult> MultigetAsync(
            string calendarHref,
            IReadOnlyList<string> resourceHrefs,
            CancellationToken cancellationToken) => new CalendarMultigetResult.Resources(
                multiget is null
                    ? []
                    : await multiget(calendarHref, resourceHrefs, cancellationToken).ConfigureAwait(false));

        public Task<CalendarResourceRead> GetAsync(
            string calendarHref,
            string resourceHref,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Direct GET is forbidden until issue #109.");
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
