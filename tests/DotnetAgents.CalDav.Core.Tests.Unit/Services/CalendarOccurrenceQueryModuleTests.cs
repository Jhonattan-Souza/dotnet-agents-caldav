using System.Text;
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

public sealed class CalendarOccurrenceQueryModuleTests
{
    private static readonly DateTimeOffset From = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(3);

    [Fact]
    public void StartExecutorsDependOnNeutralAcquisitionAndTemporalCollaborators()
    {
        var occurrenceConstructorTypes = ConstructorTypes(typeof(CalendarOccurrenceQueryStartExecutor));
        var entityConstructorTypes = ConstructorTypes(typeof(CalendarEntityQueryStartExecutor));

        occurrenceConstructorTypes.ShouldContain(typeof(CalendarQueryAcquisitionExecutor));
        occurrenceConstructorTypes.ShouldContain(typeof(CalendarTemporalContextResolver));
        occurrenceConstructorTypes.ShouldNotContain(typeof(CalendarEntityQueryStartExecutor));
        entityConstructorTypes.ShouldContain(typeof(CalendarQueryAcquisitionExecutor));
        entityConstructorTypes.ShouldContain(typeof(CalendarTemporalContextResolver));
        entityConstructorTypes.ShouldNotContain(typeof(Func<ICalendarQueryTransport>));
        entityConstructorTypes.ShouldNotContain(typeof(CalendarQueryResourceRetriever));
    }

    private static Type[] ConstructorTypes(Type type) => type.GetConstructors(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .ShouldHaveSingleItem().GetParameters().Select(parameter => parameter.ParameterType).ToArray();

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task CancelledOccurrenceFollowsExplicitInclusionPolicy(bool includeCancelled, int expectedCount)
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var href = calendarHref + "cancelled.ics";
        var transport = new OccurrenceTransport(calendarHref, [href], _ => CancelledEvent());
        await using var provider = CreateProvider(transport);

        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(new CalendarOccurrenceQuery(
                CalendarEntityScope.All,
                new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
                "UTC",
                includeCancelled)),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();

        page.Value.Items.Count.ShouldBe(expectedCount);
    }

    [Fact]
    public async Task StartFreezesTotalOrderAndContinuePerformsNoRemoteWork()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var resourceHrefs = new[] { calendarHref + "z.ics", calendarHref + "a.ics" };
        var transport = new OccurrenceTransport(calendarHref, resourceHrefs);
        await using var provider = CreateProvider(transport);
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var first = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(Query(), PageSize: 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();
        var workAfterStart = transport.TotalCalls;
        var second = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Continue(first.Value.NextCursor!, PageSize: 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();

        transport.TotalCalls.ShouldBe(workAfterStart);
        Href(first).ShouldBe(resourceHrefs[1]);
        Href(second).ShouldBe(resourceHrefs[0]);
        first.Value.PaginationMode.ShouldBe("query_result_snapshot");
        first.Value.TemporalEvaluationContext.ShouldBe(
            new TemporalEvaluationContext("America/New_York", TemporalEvaluationContextSource.Caller));
        second.Value.TemporalEvaluationContext.ShouldBe(first.Value.TemporalEvaluationContext);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(200)]
    public async Task EqualSemanticKeysTraverseByResourceHrefWithoutDuplicatesOrOmissions(int pageSize)
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var resourceHrefs = Enumerable.Range(0, 201)
            .Select(index => $"{calendarHref}{200 - index:D3}.ics")
            .ToArray();
        var transport = new OccurrenceTransport(
            calendarHref,
            resourceHrefs,
            _ => Event("shared-uid", "same-semantic-key"));
        await using var provider = CreateProvider(transport);
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var actual = new List<string>();
        QueryReply<CalendarOccurrenceQueryItem> reply = await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(Query(), pageSize),
            TestContext.Current.CancellationToken);
        while (true)
        {
            var page = reply.ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>().Value;
            actual.AddRange(page.Items.Select(ItemHref));
            if (page.NextCursor is null)
                break;
            reply = await module.QueryOccurrencesAsync(
                new CalendarOccurrenceQueryRequest.Continue(page.NextCursor, pageSize),
                TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(resourceHrefs.Order(StringComparer.Ordinal));
        actual.Distinct(StringComparer.Ordinal).Count().ShouldBe(resourceHrefs.Length);
    }

    [Fact]
    public async Task ContinueReplaysFrozenBytesAfterRemoteStateChangesAndNewStartObservesTheChange()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var resourceHrefs = new[] { calendarHref + "a.ics", calendarHref + "b.ics" };
        var summary = "before-snapshot";
        var transport = new OccurrenceTransport(
            calendarHref,
            resourceHrefs,
            _ => Event("stable-uid", summary));
        await using var provider = CreateProvider(transport);
        var module = provider.GetRequiredService<ICalendarQueryModule>();
        var first = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(Query(), 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();
        var cursor = first.Value.NextCursor.ShouldNotBeNull();
        var workAfterStart = transport.TotalCalls;

        summary = "after-snapshot";
        var frozen = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Continue(cursor, 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();
        var replay = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Continue(cursor, 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();

        transport.TotalCalls.ShouldBe(workAfterStart);
        frozen.Value.StructuredContent.GetRawText().ShouldBe(replay.Value.StructuredContent.GetRawText());
        frozen.Value.StructuredContent.GetRawText().ShouldContain("before-snapshot");
        frozen.Value.StructuredContent.GetRawText().ShouldNotContain("after-snapshot");

        var fresh = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(Query(), 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();
        fresh.Value.StructuredContent.GetRawText().ShouldContain("after-snapshot");
    }

    [Fact]
    public async Task InvalidTemporalContextFailsBeforeAnyCalDavWork()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new OccurrenceTransport(calendarHref, [calendarHref + "event.ics"]);
        await using var provider = CreateProvider(transport);
        var invalidQuery = Query() with { EvaluationTimeZone = "Private/Unknown" };

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(invalidQuery),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);
        transport.TotalCalls.ShouldBe(0);
        provider.GetRequiredService<CalendarQuerySnapshotStore>().ActiveSnapshotCount.ShouldBe(0);
    }

    [Fact]
    public async Task WeakRevisionFailsAtomicallyWithoutPublishingASnapshot()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new OccurrenceTransport(calendarHref, [calendarHref + "event.ics"])
        {
            EntityTag = "W/\"r1\""
        };
        await using var provider = CreateProvider(transport);

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(Query()),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.ConcurrencyUnavailable);
        provider.GetRequiredService<CalendarQuerySnapshotStore>().ActiveSnapshotCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(true, QueryFailureCode.TemporalUnresolved)]
    [InlineData(false, QueryFailureCode.RecurrenceUnevaluable)]
    public async Task SemanticFailureIsAtomicAndPublishesNoPartialOccurrenceSnapshot(
        bool unresolvedZone,
        QueryFailureCode expectedCode)
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var body = unresolvedZone ? UnknownZoneEvent() : UnevaluableEvent();
        var transport = new OccurrenceTransport(calendarHref, [calendarHref + "event.ics"], _ => body);
        await using var provider = CreateProvider(transport);

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(Query()),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(expectedCode);
        provider.GetRequiredService<CalendarQuerySnapshotStore>().ActiveSnapshotCount.ShouldBe(0);
    }

    [Fact]
    public async Task ContinuationAuthenticatesToolTamperExpiryAndPageBoundsWithoutRemoteWork()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var resourceHrefs = new[] { calendarHref + "a.ics", calendarHref + "b.ics" };
        var transport = new OccurrenceTransport(calendarHref, resourceHrefs);
        var time = new ManualTimeProvider(From);
        await using var provider = CreateProvider(transport, time);
        var module = provider.GetRequiredService<ICalendarQueryModule>();
        var occurrence = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(Query(), 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();
        var occurrenceCursor = occurrence.Value.NextCursor.ShouldNotBeNull();
        var entity = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.All,
                [CalendarEntityKind.Event],
                From,
                To,
                "America/New_York"), 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var entityCursor = entity.Value.NextCursor.ShouldNotBeNull();
        var workAfterStarts = transport.TotalCalls;
        var tampered = occurrenceCursor[..^1] + (occurrenceCursor[^1] == 'A' ? "B" : "A");

        var tamperFailure = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Continue(tampered, 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Failure>();
        var wrongToolFailure = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Continue(entityCursor, 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Failure>();
        var pageFailure = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Continue(occurrenceCursor, 0),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Failure>();
        time.Advance(TimeSpan.FromMinutes(11));
        var expiryFailure = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Continue(occurrenceCursor, 1),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Failure>();

        tamperFailure.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);
        wrongToolFailure.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);
        pageFailure.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);
        expiryFailure.Error.Code.ShouldBe(QueryFailureCode.CursorExpired);
        transport.TotalCalls.ShouldBe(workAfterStarts);
    }

    private static CalendarOccurrenceQuery Query() => new(
        CalendarEntityScope.All,
        From,
        To,
        "America/New_York");

    private static string? Href(QueryReply<CalendarOccurrenceQueryItem>.Page reply) => reply.Value.Items
        .ShouldHaveSingleItem().Value.GetProperty("snapshot").GetProperty("resourceRevision")
        .GetProperty("href").GetString();

    private static string ItemHref(CalendarOccurrenceQueryItem item) => item.Value.GetProperty("snapshot")
        .GetProperty("resourceRevision").GetProperty("href").GetString().ShouldNotBeNull();

    private static ServiceProvider CreateProvider(
        ICalendarQueryTransport transport,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
            options.EvaluationTimeZone = "UTC";
        });
        services.AddSingleton(Substitute.For<ICalendarClient>());
        if (timeProvider is not null)
            services.AddSingleton(timeProvider);
        services.AddSingleton(transport);
        return services.BuildServiceProvider();
    }

    private static ReadOnlyMemory<byte> CancelledEvent() => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Occurrence Module Tests//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:cancelled-event\r\nDTSTAMP:20260823T120000Z\r\n"
        + "DTSTART:20260824T120000Z\r\nDTEND:20260824T130000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:cancelled-event\r\nDTSTAMP:20260823T120000Z\r\nRECURRENCE-ID:20260825T120000Z\r\n"
        + "DTSTART:20260825T120000Z\r\nDTEND:20260825T130000Z\r\nSTATUS:CANCELLED\r\nEND:VEVENT\r\n"
        + "END:VCALENDAR\r\n");

    private static ReadOnlyMemory<byte> Event(string uid, string summary) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Occurrence Module Tests//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260823T120000Z\r\nSUMMARY:{summary}\r\n"
        + "DTSTART:20260824T120000Z\r\nDTEND:20260824T130000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static ReadOnlyMemory<byte> UnknownZoneEvent() => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Occurrence Module Tests//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:unknown-zone\r\nDTSTAMP:20260823T120000Z\r\n"
        + "DTSTART;TZID=Private/Unknown:20260824T120000\r\nDTEND;TZID=Private/Unknown:20260824T130000\r\n"
        + "END:VEVENT\r\nEND:VCALENDAR\r\n");

    private static ReadOnlyMemory<byte> UnevaluableEvent() => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Occurrence Module Tests//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:unevaluable\r\nDTSTAMP:20260823T120000Z\r\n"
        + "DTSTART:20260824T120000Z\r\nDURATION:PT0S\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"
        + "END:VEVENT\r\nEND:VCALENDAR\r\n");

    private sealed class OccurrenceTransport(
        string calendarHref,
        IReadOnlyList<string> resourceHrefs,
        Func<string, ReadOnlyMemory<byte>>? body = null)
        : ICalendarQueryTransport
    {
        internal int TotalCalls { get; private set; }
        internal string EntityTag { get; init; } = "\"r1\"";

        public Task<CalendarOperationDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
        {
            TotalCalls++;
            var calendar = new CalendarDescriptor
            {
                Href = calendarHref,
                DisplayName = "Work",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            };
            return Task.FromResult(new CalendarOperationDiscoveryResult(
                new CalendarDiscoveryResult([calendar], []),
                CalendarSelectionResult.Success(calendar),
                CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound)));
        }

        public Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
            string candidateCalendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.FromResult(resourceHrefs);
        }

        public Task<CalendarMultigetResult> MultigetAsync(
            string candidateCalendarHref,
            IReadOnlyList<string> requestedHrefs,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.FromResult<CalendarMultigetResult>(new CalendarMultigetResult.Resources(requestedHrefs
                .Select(href => CalendarResourceRead.Success(href, EntityTag, body?.Invoke(href) ?? Event(href)))
                .ToArray()));
        }

        public Task<CalendarResourceRead> GetAsync(
            string candidateCalendarHref,
            string resourceHref,
            CancellationToken cancellationToken) => throw new InvalidOperationException();

        private static ReadOnlyMemory<byte> Event(string href) => CalendarOccurrenceQueryModuleTests.Event(
            Uri.EscapeDataString(href),
            "fixture");
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => new NoOpTimer();

        internal void Advance(TimeSpan amount) => utcNow += amount;
    }

    private sealed class NoOpTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
