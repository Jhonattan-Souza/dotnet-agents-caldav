using System.Diagnostics;
using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

[Collection("ActivityListener")]
public sealed class CalendarQueryTextSearchTests
{
    private const string CalendarHref = "https://cal.example/calendars/work/";
    private static readonly DateTimeOffset From = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(30);

    [Fact]
    public async Task EntityQueryDownloadsOnlyServerCandidatesAndKeepsTheLocalMatchAuthoritative()
    {
        var transport = new TextTransport(Corpus(), _ => [Href("dentist"), Href("decoy"), Href("series")]);
        await using var provider = CreateProvider(transport);

        var page = await StartEntitiesAsync(provider, new CalendarTextFilter("DENTIST"));

        EntityUids(page).ShouldBe(["dentist", "series"]);
        transport.MultigetHrefs.Order(StringComparer.Ordinal)
            .ShouldBe([Href("decoy"), Href("dentist"), Href("series")]);
        transport.TextCalls.ShouldBe([(CalendarHref, CalendarEntityKind.Event, 4)]);
    }

    [Fact]
    public async Task VerifiedUnavailabilityFallsBackToTheUnreducedCorpusWithIdenticalResults()
    {
        var reduced = new TextTransport(Corpus(), _ => [Href("dentist"), Href("series")]);
        var unavailable = new TextTransport(Corpus(), textHrefs: null);
        var stopped = new List<Activity>();
        using var listener = ListenToQuery(stopped);
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        await using var reducedProvider = CreateProvider(reduced);
        await using var unavailableProvider = CreateProvider(unavailable);

        QueryReply<CalendarEntityQueryItem>.Page reducedPage;
        QueryReply<CalendarEntityQueryItem>.Page fallbackPage;
        using (source.StartActivity("caldav.operation", ActivityKind.Internal))
            reducedPage = await StartEntitiesAsync(reducedProvider, new CalendarTextFilter("dentist"));
        using (source.StartActivity("caldav.operation", ActivityKind.Internal))
            fallbackPage = await StartEntitiesAsync(unavailableProvider, new CalendarTextFilter("dentist"));

        fallbackPage.Value.StructuredContent.GetProperty("items").GetRawText()
            .ShouldBe(reducedPage.Value.StructuredContent.GetProperty("items").GetRawText());
        unavailable.MultigetHrefs.Count.ShouldBe(Corpus().Count);
        reduced.MultigetHrefs.Count.ShouldBe(2);
        var operations = stopped.Where(activity => activity.OperationName == "caldav.operation").ToArray();
        operations.Select(activity => activity.GetTagItem("caldav.query.text_prefilter"))
            .ShouldBe(["applied", "unavailable"]);
    }

    [Fact]
    public async Task IneligibleTextSkipsTheServerReductionAndStillFiltersLocally()
    {
        var transport = new TextTransport(Corpus(), _ => throw new InvalidOperationException("No text REPORT."));
        var stopped = new List<Activity>();
        using var listener = ListenToQuery(stopped);
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        await using var provider = CreateProvider(transport);

        QueryReply<CalendarEntityQueryItem>.Page page;
        using (source.StartActivity("caldav.operation", ActivityKind.Internal))
            page = await StartEntitiesAsync(provider, new CalendarTextFilter("AÇA"));

        EntityUids(page).ShouldBe(["accent"]);
        transport.TextCalls.ShouldBeEmpty();
        stopped.Single(activity => activity.OperationName == "caldav.operation")
            .GetTagItem("caldav.query.text_prefilter").ShouldBe("ineligible");
    }

    [Fact]
    public async Task UnfilteredQueryNeverAsksForTextCandidatesOrRecordsATextFact()
    {
        var transport = new TextTransport(Corpus(), _ => throw new InvalidOperationException("No text REPORT."));
        var stopped = new List<Activity>();
        using var listener = ListenToQuery(stopped);
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        await using var provider = CreateProvider(transport);

        QueryReply<CalendarEntityQueryItem>.Page page;
        using (source.StartActivity("caldav.operation", ActivityKind.Internal))
            page = await StartEntitiesAsync(provider, null);

        page.Value.Items.Count.ShouldBe(Corpus().Count);
        transport.TextCalls.ShouldBeEmpty();
        stopped.Single(activity => activity.OperationName == "caldav.operation")
            .GetTagItem("caldav.query.text_prefilter").ShouldBeNull();
    }

    [Fact]
    public async Task CategoriesAndTextCombineWithinOneComponentAndExcludeOpaqueResources()
    {
        var transport = new TextTransport(Corpus(), textHrefs: null);
        await using var provider = CreateProvider(transport);

        var categories = await StartEntitiesAsync(provider, new CalendarTextFilter(Categories: ["health"]));
        var combined = await StartEntitiesAsync(provider, new CalendarTextFilter("checkup", ["HEALTH", "Personal"]));
        var mismatched = await StartEntitiesAsync(provider, new CalendarTextFilter("dentist", ["personal"]));

        EntityUids(categories).ShouldBe(["dentist", "health"]);
        EntityUids(combined).ShouldBe(["health"]);
        EntityUids(mismatched).ShouldBeEmpty();
    }

    [Fact]
    public async Task TextMismatchedResourceIsDroppedBeforeTemporalEvaluationCanFail()
    {
        var resources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Href("dentist")] = Event("dentist", "SUMMARY:Dentist\r\n"),
            [Href("unresolved")] = Event("unresolved", "SUMMARY:Other\r\n", "DTSTART;TZID=Private/Unknown:20261102T090000")
        };
        var transport = new TextTransport(resources, textHrefs: null);
        await using var provider = CreateProvider(transport);
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var filtered = await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(new CalendarOccurrenceQuery(
                CalendarEntityScope.All, From, To, "UTC", TextFilter: new CalendarTextFilter("dentist"))),
            CancellationToken.None);
        var unfiltered = await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(new CalendarOccurrenceQuery(CalendarEntityScope.All, From, To, "UTC")),
            CancellationToken.None);
        var entities = await StartEntitiesAsync(provider, new CalendarTextFilter("dentist"), windowed: true);

        filtered.ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>().Value.Items.Count.ShouldBe(1);
        unfiltered.ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Failure>().Error.Code
            .ShouldBe(QueryFailureCode.TemporalUnresolved);
        EntityUids(entities).ShouldBe(["dentist"]);
    }

    [Fact]
    public async Task OccurrencesMatchTheirOwnOverrideAndMasterWithoutInheritance()
    {
        var transport = new TextTransport(Corpus(), textHrefs: null);
        await using var provider = CreateProvider(transport);

        var dentist = await StartOccurrencesAsync(provider, new CalendarTextFilter("dentist"));
        var weekly = await StartOccurrencesAsync(provider, new CalendarTextFilter("weekly planning"));

        OccurrenceIdentities(dentist, "series").ShouldBe(["2026-11-03T09:00:00Z"]);
        OccurrenceIdentities(weekly, "series")
            .ShouldBe(["2026-11-02T09:00:00Z", "2026-11-04T09:00:00Z", "2026-11-05T09:00:00Z", "2026-11-06T09:00:00Z"]);
        dentist.Value.Items.Select(item => Uid(item.Value.GetProperty("snapshot")))
            .ShouldBe(["dentist", "series"]);
    }

    [Fact]
    public async Task OccurrenceCursorContinuesTheFrozenFilteredSnapshotWithoutRemoteWork()
    {
        var transport = new TextTransport(Corpus(), _ => [Href("series")]);
        await using var provider = CreateProvider(transport);
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var first = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(
                new CalendarOccurrenceQuery(
                    CalendarEntityScope.All, From, To, "UTC", TextFilter: new CalendarTextFilter("planning")),
                PageSize: 2),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();
        var calls = transport.TotalCalls;
        var rest = (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Continue(first.Value.NextCursor.ShouldNotBeNull(), 200),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();

        (first.Value.Items.Count + rest.Value.Items.Count).ShouldBe(5);
        rest.Value.NextCursor.ShouldBeNull();
        transport.TotalCalls.ShouldBe(calls);
    }

    [Fact]
    public async Task TodoRowsMatchEntityOverridesAndOccurrenceEffectiveComponents()
    {
        var resources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Href("chores")] = RecurringTodo(),
            [Href("dentist-todo")] = Todo("dentist-todo", "SUMMARY:Call dentist\r\n"),
            [Href("indeterminate")] = Todo("indeterminate", "SUMMARY:Other\r\nSTATUS:COMPLETED\r\nPERCENT-COMPLETE:10\r\n"),
            [Href("opaque")] = Opaque()
        };
        var transport = new TextTransport(resources, textHrefs: null);
        await using var provider = CreateProvider(transport);
        var module = provider.GetRequiredService<ICalendarQueryModule>();
        CalendarTodoCompletionState[] allStates =
        [
            CalendarTodoCompletionState.Open,
            CalendarTodoCompletionState.Completed,
            CalendarTodoCompletionState.Cancelled,
            CalendarTodoCompletionState.Indeterminate
        ];

        var entityRows = await StartTodosAsync(module, new CalendarTodoQuery(
            CalendarEntityScope.All, allStates, TextFilter: new CalendarTextFilter("garden")));
        var occurrenceRows = await StartTodosAsync(module, new CalendarTodoQuery(
            CalendarEntityScope.All, allStates, From, To, TextFilter: new CalendarTextFilter("garden")));
        var openDefault = await StartTodosAsync(module, new CalendarTodoQuery(
            CalendarEntityScope.All, TextFilter: new CalendarTextFilter("dentist")));
        var unfiltered = await StartTodosAsync(module, new CalendarTodoQuery(CalendarEntityScope.All));

        entityRows.Value.Items.Select(item => item.Value.GetProperty("uid").GetString()).ShouldBe(["chores"]);
        occurrenceRows.Value.Items.Select(item => item.Value.GetProperty("completionTarget")
                .GetProperty("recurrenceIdentity").GetProperty("value").GetString())
            .ShouldBe(["2026-11-03T09:00:00Z", "2026-11-04T09:00:00Z"]);
        openDefault.Value.Items.Select(item => item.Value.GetProperty("uid").GetString()).ShouldBe(["dentist-todo"]);
        openDefault.Value.StructuredContent.GetProperty("excludedIndeterminateCount").GetInt32().ShouldBe(0);
        unfiltered.Value.StructuredContent.GetProperty("excludedIndeterminateCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task TodoKindEntityAndOccurrenceQueriesMatchTodoComponents()
    {
        var resources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Href("chores")] = RecurringTodo(),
            [Href("dentist")] = Event("dentist", "SUMMARY:Garden party\r\n")
        };
        var transport = new TextTransport(resources, textHrefs: null);
        await using var provider = CreateProvider(transport);
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var entities = (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.All,
                [CalendarEntityKind.Todo],
                TextFilter: new CalendarTextFilter("garden"))),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        var occurrences = await StartOccurrencesAsync(provider, new CalendarTextFilter("water garden"));

        EntityUids(entities).ShouldBe(["chores"]);
        OccurrenceIdentities(occurrences, "chores").ShouldBe(["2026-11-03T09:00:00Z", "2026-11-04T09:00:00Z"]);
        OccurrenceIdentities(occurrences, "dentist").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(QueryFamily.Entity)]
    [InlineData(QueryFamily.Occurrence)]
    [InlineData(QueryFamily.Todo)]
    public async Task InvalidTextFilterFailsBeforeAnyCalDavWork(QueryFamily family)
    {
        var transport = new TextTransport(Corpus(), textHrefs: null);
        await using var provider = CreateProvider(transport);
        var module = provider.GetRequiredService<ICalendarQueryModule>();
        var filter = new CalendarTextFilter("   ", ["Work"]);

        var failure = family switch
        {
            QueryFamily.Entity => (await module.QueryEntitiesAsync(
                new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                    CalendarEntityScope.All, [CalendarEntityKind.Event], TextFilter: filter)),
                CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>().Error,
            QueryFamily.Occurrence => (await module.QueryOccurrencesAsync(
                new CalendarOccurrenceQueryRequest.Start(new CalendarOccurrenceQuery(
                    CalendarEntityScope.All, From, To, "UTC", TextFilter: filter)),
                CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Failure>().Error,
            _ => (await module.QueryTodosAsync(
                new CalendarTodoQueryRequest.Start(
                    new CalendarTodoQuery(CalendarEntityScope.All, TextFilter: filter),
                    [CalendarTodoProjectionField.Summary]),
                CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Failure>().Error
        };

        failure.Code.ShouldBe(QueryFailureCode.InvalidInput);
        failure.Message.ShouldContain("text or categories filter is invalid");
        transport.TotalCalls.ShouldBe(0);
    }

    public enum QueryFamily
    {
        Entity,
        Occurrence,
        Todo
    }

    private static async Task<QueryReply<CalendarEntityQueryItem>.Page> StartEntitiesAsync(
        ServiceProvider provider,
        CalendarTextFilter? filter,
        bool windowed = false) => (await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.All,
                [CalendarEntityKind.Event],
                windowed ? From : null,
                windowed ? To : null,
                windowed ? "UTC" : null,
                filter), PageSize: 200),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

    private static async Task<QueryReply<CalendarOccurrenceQueryItem>.Page> StartOccurrencesAsync(
        ServiceProvider provider,
        CalendarTextFilter filter) => (await provider.GetRequiredService<ICalendarQueryModule>().QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(
                new CalendarOccurrenceQuery(CalendarEntityScope.All, From, To, "UTC", TextFilter: filter),
                PageSize: 200),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>();

    private static async Task<QueryReply<CalendarTodoQueryPageItem>.Page> StartTodosAsync(
        ICalendarQueryModule module,
        CalendarTodoQuery query) => (await module.QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                query with { EvaluationTimeZone = "UTC" },
                [CalendarTodoProjectionField.Summary],
                PageSize: 200),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();

    private static string[] EntityUids(QueryReply<CalendarEntityQueryItem>.Page page) =>
        page.Value.Items.Select(item => Uid(item.Value)).Order(StringComparer.Ordinal).ToArray();

    private static string Uid(System.Text.Json.JsonElement snapshot) =>
        snapshot.GetProperty("projection").GetProperty("uid").GetString()!;

    private static string[] OccurrenceIdentities(QueryReply<CalendarOccurrenceQueryItem>.Page page, string uid) =>
        page.Value.Items
            .Where(item => Uid(item.Value.GetProperty("snapshot")) == uid)
            .Select(item => item.Value.GetProperty("recurrenceIdentity").GetProperty("value").GetProperty("value")
                .GetString()!)
            .ToArray();

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

    private static ServiceProvider CreateProvider(ICalendarQueryTransport transport)
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
        services.AddSingleton(transport);
        return services.BuildServiceProvider();
    }

    private static string Href(string name) => $"{CalendarHref}{name}.ics";

    private static Dictionary<string, string> Corpus() => new(StringComparer.Ordinal)
    {
        [Href("dentist")] = Event("dentist", "SUMMARY:Dentist appointment\r\nCATEGORIES:Health\r\n"),
        [Href("health")] = Event("health", "SUMMARY:Annual checkup\r\nCATEGORIES:Health,Personal\r\n"),
        [Href("decoy")] = Event("decoy", "SUMMARY:Team sync\r\nDESCRIPTION:d-entist is not a match\r\n"),
        [Href("accent")] = Event("accent", "LOCATION:Praça central\r\n"),
        [Href("series")] = RecurringEvent(),
        [Href("opaque")] = Opaque()
    };

    private static string Event(string uid, string lines, string start = "DTSTART:20261102T090000Z") =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Text Search//EN\r\nBEGIN:VEVENT\r\n"
        + $"UID:{uid}\r\nDTSTAMP:20260815T120000Z\r\n{start}\r\nDURATION:PT1H\r\n{lines}END:VEVENT\r\nEND:VCALENDAR\r\n";

    private static string RecurringEvent() =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Text Search//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:series\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20261102T090000Z\r\n"
        + "DURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=5\r\nSUMMARY:Weekly planning\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:series\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20261103T090000Z\r\n"
        + "DTSTART:20261103T150000Z\r\nDURATION:PT1H\r\nSUMMARY:Planning with dentist\r\nEND:VEVENT\r\n"
        + "END:VCALENDAR\r\n";

    private static string Todo(string uid, string lines) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Text Search//EN\r\nBEGIN:VTODO\r\n"
        + $"UID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDUE:20261102T090000Z\r\n{lines}END:VTODO\r\nEND:VCALENDAR\r\n";

    private static string RecurringTodo() =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Text Search//EN\r\n"
        + "BEGIN:VTODO\r\nUID:chores\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20261102T090000Z\r\n"
        + "RRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Water plants\r\nEND:VTODO\r\n"
        + "BEGIN:VTODO\r\nUID:chores\r\nDTSTAMP:20260815T120000Z\r\n"
        + "RECURRENCE-ID;RANGE=THISANDFUTURE:20261103T090000Z\r\nDTSTART:20261103T090000Z\r\n"
        + "SUMMARY:Water garden\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private static string Opaque() =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Text Search//EN\r\nBEGIN:VJOURNAL\r\n"
        + "UID:opaque\r\nDTSTAMP:20260815T120000Z\r\nSUMMARY:dentist planning garden\r\nEND:VJOURNAL\r\nEND:VCALENDAR\r\n";

    private sealed class TextTransport(
        IReadOnlyDictionary<string, string> resources,
        Func<CalendarEntityKind, IReadOnlyList<string>>? textHrefs) : ICalendarQueryTransport
    {
        internal List<string> MultigetHrefs { get; } = [];

        internal List<(string Calendar, CalendarEntityKind Kind, int Branches)> TextCalls { get; } = [];

        internal int TotalCalls { get; private set; }

        public Task<CalendarOperationDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
        {
            TotalCalls++;
            var calendar = new CalendarDescriptor
            {
                Href = CalendarHref,
                DisplayName = "Work",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.Advertised
            };
            return Task.FromResult(CalendarOperationDiscoveryResultFactory.Create(
                new CalendarDiscoveryResult([calendar], []),
                CalendarSelectionResult.Success(calendar),
                CalendarSelectionResult.Success(calendar)));
        }

        public Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
            string calendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            return Task.FromResult<IReadOnlyList<string>>(resources.Keys.Order(StringComparer.Ordinal).ToArray());
        }

        public Task<CalendarTextCandidateResult> QueryTextCandidateHrefsAsync(
            string calendarHref,
            CalendarEntityKind entityKind,
            CalendarTextPrefilter prefilter,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            TextCalls.Add((calendarHref, entityKind, prefilter.Branches.Count));
            return Task.FromResult<CalendarTextCandidateResult>(textHrefs is null
                ? new CalendarTextCandidateResult.VerifiedUnavailable()
                : new CalendarTextCandidateResult.Hrefs(textHrefs(entityKind).ToHashSet(StringComparer.Ordinal)));
        }

        public Task<CalendarMultigetResult> MultigetAsync(
            string calendarHref,
            IReadOnlyList<string> resourceHrefs,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            MultigetHrefs.AddRange(resourceHrefs);
            return Task.FromResult<CalendarMultigetResult>(new CalendarMultigetResult.Resources(resourceHrefs
                .Select(href => CalendarResourceRead.Success(href, "\"r1\"", Encoding.UTF8.GetBytes(resources[href])))
                .ToArray()));
        }

        public Task<CalendarResourceRead> GetAsync(
            string calendarHref,
            string resourceHref,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Direct GET is not scripted.");
    }
}
