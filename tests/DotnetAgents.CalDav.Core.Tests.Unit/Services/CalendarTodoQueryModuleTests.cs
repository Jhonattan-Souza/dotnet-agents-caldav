using System.Text;
using System.Diagnostics;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using DotnetAgents.CalDav.Core.Configuration;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

[Collection("ActivityListener")]
public sealed class CalendarTodoQueryModuleTests
{
    private static readonly DateTimeOffset From = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(2);

    [Fact]
    public async Task TodoStartWithoutTemporalContextFailsBeforeTransportConstruction()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new TodoTransport(calendarHref, [calendarHref + "dated.ics"]);
        var constructionCount = 0;
        await using var provider = CreateProvider(
            transport,
            services => services.AddTransient<ICalendarQueryTransport>(_ =>
            {
                constructionCount++;
                return transport;
            }),
            options => options.EvaluationTimeZone = null);

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(CalendarEntityScope.All),
                [CalendarTodoProjectionField.Summary]),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.InvalidInput);
        constructionCount.ShouldBe(0);
        transport.DiscoveryCount.ShouldBe(0);
        provider.GetRequiredService<CalendarQuerySnapshotStore>().ActiveSnapshotCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak-r1\"")]
    public async Task MissingOrWeakEntityTagFailsBeforeSemanticExclusion(string? entityTag)
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new TodoTransport(
            calendarHref,
            [calendarHref + "dated.ics"],
            entityTag);
        await using var provider = CreateProvider(transport);

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(
                    CalendarEntityScope.All,
                    [CalendarTodoCompletionState.Completed]),
                [CalendarTodoProjectionField.Summary]),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.ConcurrencyUnavailable);
        provider.GetRequiredService<CalendarQuerySnapshotStore>().ActiveSnapshotCount.ShouldBe(0);
    }

    [Fact]
    public async Task ContinueReplaysFrozenPageWithZeroRemoteOrSemanticWork()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new TodoTransport(calendarHref,
        [
            calendarHref + "dated.ics",
            calendarHref + "undated.ics",
            calendarHref + "recurring.ics"
        ]);
        await using var provider = CreateProvider(transport);
        var module = provider.GetRequiredService<ICalendarQueryModule>();
        using var listener = Listen([]);
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        QueryReply<CalendarTodoQueryPageItem>.Page first;
        Activity startOperation;
        using (var operation = source.StartActivity("caldav.operation", ActivityKind.Internal))
        {
            startOperation = operation!;
            first = (await module.QueryTodosAsync(
                new CalendarTodoQueryRequest.Start(
                    new CalendarTodoQuery(CalendarEntityScope.All),
                    [CalendarTodoProjectionField.Summary],
                    PageSize: 1),
                CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();
        }
        var cursor = first.Value.NextCursor.ShouldNotBeNull();
        var counts = (transport.DiscoveryCount, transport.CandidateKinds.Count, transport.MultigetRequests.Count);
        QueryReply<CalendarTodoQueryPageItem>.Page continued;
        Activity continueOperation;
        using (var operation = source.StartActivity("caldav.operation", ActivityKind.Internal))
        {
            continueOperation = operation!;
            continued = (await module.QueryTodosAsync(
                new CalendarTodoQueryRequest.Continue(cursor, PageSize: 1),
                CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();
        }
        var replay = (await module.QueryTodosAsync(
            new CalendarTodoQueryRequest.Continue(cursor, PageSize: 1),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();

        (transport.DiscoveryCount, transport.CandidateKinds.Count, transport.MultigetRequests.Count)
            .ShouldBe(counts);
        replay.Value.StructuredContent.GetRawText().ShouldBe(continued.Value.StructuredContent.GetRawText());
        startOperation.GetTagItem("caldav.query.evaluation_count").ShouldNotBeNull();
        continueOperation.GetTagItem("caldav.query.mode").ShouldBe("continue");
        continueOperation.GetTagItem("caldav.query.snapshot_lookup_count").ShouldBe(1L);
        continueOperation.GetTagItem("caldav.query.page_admission_count").ShouldBe(1L);
        continueOperation.GetTagItem("caldav.query.evaluation_count").ShouldBeNull();
        continueOperation.GetTagItem("caldav.query.serialization_count").ShouldBeNull();
    }

    [Fact]
    public async Task CompletionAndDueFiltersObserveTheChangedOverrideAfterResolution()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new TodoTransport(calendarHref, [calendarHref + "recurring.ics"]);
        await using var provider = CreateProvider(transport);

        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(
                    CalendarEntityScope.All,
                    [CalendarTodoCompletionState.Completed],
                    From,
                    To,
                    "UTC",
                    new DateTimeOffset(2026, 8, 25, 14, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 25, 16, 0, 0, TimeSpan.Zero)),
                [
                    CalendarTodoProjectionField.Summary,
                    CalendarTodoProjectionField.Status,
                    CalendarTodoProjectionField.Due
                ]),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();

        var item = page.Value.StructuredContent.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("resultKind").GetString().ShouldBe("occurrence");
        item.GetProperty("completionState").GetString().ShouldBe("completed");
        item.GetProperty("summary").GetString().ShouldBe("Changed override");
        item.GetProperty("due").GetProperty("evaluatedUtc").GetProperty("value").GetString()
            .ShouldBe("2026-08-25T15:00:00Z");
    }

    [Fact]
    public async Task UnevaluableRecurringTodoFailsAtomicallyWithoutSnapshot()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new TodoTransport(
            calendarHref,
            [calendarHref + "invalid.ics"],
            content: _ => Ics(
                "BEGIN:VTODO\r\nUID:invalid\r\nDTSTAMP:20260823T120000Z\r\n"
                + "DUE:20260824T110000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VTODO\r\n"));
        await using var provider = CreateProvider(transport);

        var failure = (await provider.GetRequiredService<ICalendarQueryModule>().QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(CalendarEntityScope.All, From: From, To: To, EvaluationTimeZone: "UTC"),
                [CalendarTodoProjectionField.Summary]),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.RecurrenceUnevaluable);
        provider.GetRequiredService<CalendarQuerySnapshotStore>().ActiveSnapshotCount.ShouldBe(0);
    }

    [Fact]
    public async Task GlobalOrderTraversesEquivalentlyAtEverySupportedRepresentativePageSize()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new TodoTransport(calendarHref,
        [
            calendarHref + "undated.ics",
            calendarHref + "recurring.ics",
            calendarHref + "dated.ics"
        ]);
        await using var provider = CreateProvider(transport);
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var one = await TraverseAsync(module, 1);
        var fifty = await TraverseAsync(module, 50);
        var twoHundred = await TraverseAsync(module, 200);

        fifty.Select(item => item.GetRawText()).ShouldBe(one.Select(item => item.GetRawText()));
        twoHundred.Select(item => item.GetRawText()).ShouldBe(one.Select(item => item.GetRawText()));
        one.Select(item => $"{item.GetProperty("uid").GetString()}:{item.GetProperty("resultKind").GetString()}")
            .ShouldBe([
                "recurring:occurrence",
                "dated:entity",
                "recurring:occurrence",
                "undated:entity"
            ]);
    }

    [Fact]
    public async Task TodoStartUsesOneVtodoCorpusAndSplitsEntityAndOccurrenceLanes()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var hrefs = new[]
        {
            calendarHref + "dated.ics",
            calendarHref + "undated.ics",
            calendarHref + "recurring.ics"
        };
        var transport = new TodoTransport(calendarHref, hrefs);
        await using var provider = CreateProvider(transport);
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");

        QueryReply<CalendarTodoQueryPageItem>.Page reply;
        using (source.StartActivity("caldav.operation", ActivityKind.Internal))
        {
            reply = (await provider.GetRequiredService<ICalendarQueryModule>().QueryTodosAsync(
                new CalendarTodoQueryRequest.Start(
                    new CalendarTodoQuery(
                        CalendarEntityScope.All,
                        [
                            CalendarTodoCompletionState.Open,
                            CalendarTodoCompletionState.Completed,
                            CalendarTodoCompletionState.Cancelled,
                            CalendarTodoCompletionState.Indeterminate
                        ],
                        From,
                        To,
                        "UTC"),
                    Enum.GetValues<CalendarTodoProjectionField>()),
                CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();
        }

        transport.DiscoveryCount.ShouldBe(1);
        transport.CandidateKinds.ShouldBe([CalendarEntityKind.Todo]);
        transport.MultigetRequests.ShouldBe([hrefs.Order(StringComparer.Ordinal).ToArray()]);
        var items = reply.Value.StructuredContent.GetProperty("items").EnumerateArray().ToArray();
        items.Length.ShouldBe(4);
        items.Count(item => item.GetProperty("resultKind").GetString() == "entity").ShouldBe(2);
        items.Count(item => item.GetProperty("resultKind").GetString() == "occurrence").ShouldBe(2);
        items.Where(item => item.GetProperty("resultKind").GetString() == "entity")
            .Select(item => item.GetProperty("completionTarget").TryGetProperty("recurrenceIdentity", out _))
            .ShouldAllBe(hasIdentity => !hasIdentity);
        items.Where(item => item.GetProperty("resultKind").GetString() == "occurrence")
            .ShouldAllBe(item => item.GetProperty("completionTarget").GetProperty("recurrenceIdentity").ValueKind != System.Text.Json.JsonValueKind.Null);
        items.Select(item => item.GetProperty("completionTarget").GetProperty("entityRevision")
                .GetProperty("entityTag").GetString())
            .ShouldAllBe(entityTag => entityTag == "\"strong-r1\"");
        reply.Value.PaginationMode.ShouldBe("query_result_snapshot");
        reply.Value.TemporalEvaluationContext.ShouldBe(new TemporalEvaluationContext(
            "UTC",
            TemporalEvaluationContextSource.Caller));
        var operation = stopped.Single(activity => activity.OperationName == "caldav.operation");
        operation.GetTagItem("caldav.query.snapshot_count").ShouldBe(3L);
        operation.GetTagItem("caldav.query.parse_count").ShouldBe(3L);
        operation.GetTagItem("caldav.query.evaluation_count").ShouldBe(3L);
        operation.GetTagItem("caldav.query.serialization_count").ShouldBe(4L);
    }

    [Fact]
    public async Task IndeterminateFilterRetainsCancelledOverrideUntilAuthoritativeClassification()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new TodoTransport(
            calendarHref,
            [calendarHref + "contradictory.ics"],
            content: _ => Ics(
                "BEGIN:VTODO\r\nUID:contradictory\r\nDTSTAMP:20260823T120000Z\r\n"
                + "DTSTART:20260824T100000Z\r\nDUE:20260824T110000Z\r\n"
                + "RRULE:FREQ=DAILY;COUNT=2\r\nEND:VTODO\r\n"
                + "BEGIN:VTODO\r\nUID:contradictory\r\nRECURRENCE-ID:20260825T100000Z\r\n"
                + "DTSTAMP:20260823T120000Z\r\nDTSTART:20260825T100000Z\r\nDUE:20260825T110000Z\r\n"
                + "STATUS:CANCELLED\r\nCOMPLETED:20260825T110000Z\r\nEND:VTODO\r\n"));
        await using var provider = CreateProvider(transport);

        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(
                    CalendarEntityScope.All,
                    [CalendarTodoCompletionState.Indeterminate],
                    From,
                    To,
                    "UTC"),
                [CalendarTodoProjectionField.Status, CalendarTodoProjectionField.CompletedAt]),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();

        var item = page.Value.StructuredContent.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("resultKind").GetString().ShouldBe("occurrence");
        item.GetProperty("completionState").GetString().ShouldBe("indeterminate");
        item.GetProperty("status").GetProperty("kind").GetString().ShouldBe("cancelled");
    }

    [Fact]
    public async Task MovedCompleteOverrideWithoutDueRemainsDueLessAndDueFilterExcludesIt()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new TodoTransport(
            calendarHref,
            [calendarHref + "moved.ics"],
            content: _ => Ics(
                "BEGIN:VTODO\r\nUID:moved\r\nDTSTAMP:20260823T120000Z\r\n"
                + "DTSTART:20260824T100000Z\r\nDUE:20260824T110000Z\r\n"
                + "RRULE:FREQ=DAILY;COUNT=2\r\nEND:VTODO\r\n"
                + "BEGIN:VTODO\r\nUID:moved\r\nRECURRENCE-ID:20260825T100000Z\r\n"
                + "DTSTAMP:20260823T120000Z\r\nDTSTART:20260825T140000Z\r\nEND:VTODO\r\n"));
        await using var provider = CreateProvider(transport);

        var module = provider.GetRequiredService<ICalendarQueryModule>();
        var page = (await module.QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(
                    CalendarEntityScope.All,
                    From: From,
                    To: To,
                    EvaluationTimeZone: "UTC"),
                [CalendarTodoProjectionField.Due, CalendarTodoProjectionField.Start]),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();

        var moved = page.Value.StructuredContent.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("start").GetProperty("evaluatedUtc").GetProperty("value").GetString()
                == "2026-08-25T14:00:00Z");
        moved.TryGetProperty("due", out _).ShouldBeFalse();

        var filtered = (await module.QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(
                    CalendarEntityScope.All,
                    From: From,
                    To: To,
                    EvaluationTimeZone: "UTC",
                    DueFrom: new DateTimeOffset(2026, 8, 25, 14, 30, 0, TimeSpan.Zero),
                    DueTo: new DateTimeOffset(2026, 8, 25, 15, 30, 0, TimeSpan.Zero)),
                [CalendarTodoProjectionField.Due]),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();
        filtered.Value.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task DueOnlyDetachedOverrideKeepsDueRoleThroughFilteringProjectionAndOrdering()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new TodoTransport(
            calendarHref,
            [calendarHref + "detached.ics", calendarHref + "later.ics"],
            content: href => href.EndsWith("detached.ics", StringComparison.Ordinal)
                ? Ics(
                    "BEGIN:VTODO\r\nUID:detached\r\nDTSTAMP:20260815T120000Z\r\n"
                    + "DTSTART:20260814T100000Z\r\nDUE:20260814T110000Z\r\nRRULE:FREQ=DAILY;COUNT=1\r\nEND:VTODO\r\n"
                    + "BEGIN:VTODO\r\nUID:detached\r\nDTSTAMP:20260815T120000Z\r\n"
                    + "RECURRENCE-ID:20260820T100000Z\r\nDUE:20260821T150000Z\r\nEND:VTODO\r\n")
                : Ics(
                    "BEGIN:VTODO\r\nUID:later\r\nDTSTAMP:20260815T120000Z\r\n"
                    + "DUE:20260822T150000Z\r\nEND:VTODO\r\n"));
        await using var provider = CreateProvider(transport);

        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(
                    CalendarEntityScope.All,
                    From: new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero),
                    To: new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero),
                    EvaluationTimeZone: "UTC",
                    DueFrom: new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero),
                    DueTo: new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero)),
                [CalendarTodoProjectionField.Due, CalendarTodoProjectionField.Start]),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();

        var items = page.Value.StructuredContent.GetProperty("items").EnumerateArray().ToArray();
        items.Select(item => item.GetProperty("uid").GetString()).ShouldBe(["detached", "later"]);
        items[0].GetProperty("due").GetProperty("source").GetProperty("value").GetString()
            .ShouldBe("2026-08-21T15:00:00Z");
        items[0].GetProperty("due").GetProperty("evaluatedUtc").GetProperty("value").GetString()
            .ShouldBe("2026-08-21T15:00:00Z");
        items[0].TryGetProperty("start", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task CompletionOnlyOverrideKeepsNominalTimingThroughDueFilterAndOrdering()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new TodoTransport(
            calendarHref,
            [calendarHref + "completion-only.ics", calendarHref + "later-completed.ics"],
            content: href => href.EndsWith("completion-only.ics", StringComparison.Ordinal)
                ? Ics(
                    "BEGIN:VTODO\r\nUID:completion-only\r\nDTSTAMP:20260823T120000Z\r\n"
                    + "DTSTART:20260824T100000Z\r\nDUE:20260824T110000Z\r\n"
                    + "RRULE:FREQ=DAILY;COUNT=2\r\nEND:VTODO\r\n"
                    + "BEGIN:VTODO\r\nUID:completion-only\r\nDTSTAMP:20260823T120000Z\r\n"
                    + "RECURRENCE-ID:20260825T100000Z\r\nSTATUS:COMPLETED\r\n"
                    + "COMPLETED:20260825T110000Z\r\nEND:VTODO\r\n")
                : Ics(
                    "BEGIN:VTODO\r\nUID:later-completed\r\nDTSTAMP:20260823T120000Z\r\n"
                    + "DTSTART:20260825T113000Z\r\nDUE:20260825T120000Z\r\n"
                    + "STATUS:COMPLETED\r\nCOMPLETED:20260825T120000Z\r\nEND:VTODO\r\n"));
        await using var provider = CreateProvider(transport);

        var page = (await provider.GetRequiredService<ICalendarQueryModule>().QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(
                    CalendarEntityScope.All,
                    [CalendarTodoCompletionState.Completed],
                    From,
                    To,
                    "UTC",
                    new DateTimeOffset(2026, 8, 25, 10, 30, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 25, 13, 0, 0, TimeSpan.Zero)),
                [CalendarTodoProjectionField.Due, CalendarTodoProjectionField.Start]),
            CancellationToken.None)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();

        var items = page.Value.StructuredContent.GetProperty("items").EnumerateArray().ToArray();
        items.Select(item => item.GetProperty("uid").GetString())
            .ShouldBe(["completion-only", "later-completed"]);
        items[0].GetProperty("start").GetProperty("evaluatedUtc").GetProperty("value").GetString()
            .ShouldBe("2026-08-25T10:00:00Z");
        items[0].GetProperty("due").GetProperty("evaluatedUtc").GetProperty("value").GetString()
            .ShouldBe("2026-08-25T11:00:00Z");
    }

    [Fact]
    public async Task TodoStartWithoutWindowKeepsRecurringMasterAsCompactEntityWithoutExpansion()
    {
        const string calendarHref = "https://cal.example/calendars/work/";
        var transport = new TodoTransport(calendarHref, [calendarHref + "recurring.ics"]);
        await using var provider = CreateProvider(transport);

        var rawReply = await provider.GetRequiredService<ICalendarQueryModule>().QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(CalendarEntityScope.All),
                [CalendarTodoProjectionField.Summary]),
            CancellationToken.None);
        if (rawReply is QueryReply<CalendarTodoQueryPageItem>.Failure failure)
            throw new InvalidOperationException($"Unexpected {failure.Error.Code}: {failure.Error.Message}");
        var reply = rawReply.ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();

        var item = reply.Value.StructuredContent.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("resultKind").GetString().ShouldBe("entity");
        item.GetProperty("completionTarget").GetProperty("kind").GetString().ShouldBe("occurrence_required");
        reply.Value.TemporalEvaluationContext.ShouldBe(new TemporalEvaluationContext(
            "UTC",
            TemporalEvaluationContextSource.Configuration));
        transport.CandidateWindows.ShouldBe([(null, null)]);
    }

    private static ServiceProvider CreateProvider(
        ICalendarQueryTransport transport,
        Action<IServiceCollection>? configureServices = null,
        Action<CalDavOptions>? configureOptions = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
            options.EvaluationTimeZone = "UTC";
            configureOptions?.Invoke(options);
        });
        services.AddSingleton(Substitute.For<ICalendarClient>());
        services.AddSingleton(transport);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static ActivityListener Listen(ICollection<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarQueryTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static async Task<IReadOnlyList<JsonElement>> TraverseAsync(
        ICalendarQueryModule module,
        int pageSize)
    {
        var request = new CalendarTodoQueryRequest.Start(
            new CalendarTodoQuery(
                CalendarEntityScope.All,
                [
                    CalendarTodoCompletionState.Open,
                    CalendarTodoCompletionState.Completed,
                    CalendarTodoCompletionState.Cancelled,
                    CalendarTodoCompletionState.Indeterminate
                ],
                From,
                To,
                "UTC"),
            [CalendarTodoProjectionField.Summary],
            pageSize);
        QueryReply<CalendarTodoQueryPageItem> reply = await module.QueryTodosAsync(request, CancellationToken.None);
        var items = new List<JsonElement>();
        while (reply is QueryReply<CalendarTodoQueryPageItem>.Page page)
        {
            items.AddRange(page.Value.Items.Select(item => item.Value));
            if (page.Value.NextCursor is null)
                break;
            reply = await module.QueryTodosAsync(
                new CalendarTodoQueryRequest.Continue(page.Value.NextCursor, pageSize),
                CancellationToken.None);
        }
        reply.ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();
        return items;
    }

    private sealed class TodoTransport(
        string calendarHref,
        IReadOnlyList<string> hrefs,
        string? entityTag = "\"strong-r1\"",
        Func<string, ReadOnlyMemory<byte>>? content = null) : ICalendarQueryTransport
    {
        internal int DiscoveryCount { get; private set; }

        internal List<CalendarEntityKind> CandidateKinds { get; } = [];

        internal List<(DateTimeOffset? From, DateTimeOffset? To)> CandidateWindows { get; } = [];

        internal List<IReadOnlyList<string>> MultigetRequests { get; } = [];

        public Task<CalendarOperationDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscoveryCount++;
            var calendar = new CalendarDescriptor
            {
                Href = calendarHref,
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
            string requestedCalendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requestedCalendarHref.ShouldBe(calendarHref);
            CandidateKinds.Add(entityKind);
            CandidateWindows.Add((from, to));
            return Task.FromResult(entityKind == CalendarEntityKind.Todo
                ? hrefs
                : (IReadOnlyList<string>)[calendarHref + "irrelevant-event.ics"]);
        }

        public Task<CalendarMultigetResult> MultigetAsync(
            string requestedCalendarHref,
            IReadOnlyList<string> resourceHrefs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requestedCalendarHref.ShouldBe(calendarHref);
            MultigetRequests.Add(resourceHrefs.ToArray());
            CalendarQueryTelemetry.ObserveMultigetAttempt(resourceHrefs.Count);
            return Task.FromResult<CalendarMultigetResult>(new CalendarMultigetResult.Resources(resourceHrefs
                .Select(href => new CalendarResourceRead(
                    CalendarResourceReadCode.Success,
                    href,
                    entityTag,
                    content?.Invoke(href) ?? Content(href)))
                .ToArray()));
        }

        public Task<CalendarResourceRead> GetAsync(
            string requestedCalendarHref,
            string resourceHref,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Conforming multiget must not use GET.");

        private static ReadOnlyMemory<byte> Content(string href)
        {
            var component = href.EndsWith("undated.ics", StringComparison.Ordinal)
                ? "BEGIN:VTODO\r\nUID:undated\r\nDTSTAMP:20260823T120000Z\r\nSUMMARY:Undated\r\nEND:VTODO\r\n"
                : href.EndsWith("dated.ics", StringComparison.Ordinal)
                    ? "BEGIN:VTODO\r\nUID:dated\r\nDTSTAMP:20260823T120000Z\r\nSUMMARY:Dated\r\nDTSTART:20260824T120000Z\r\nDUE:20260824T130000Z\r\nEND:VTODO\r\n"
                    : "BEGIN:VTODO\r\nUID:recurring\r\nDTSTAMP:20260823T120000Z\r\nSUMMARY:Recurring\r\nDTSTART:20260824T100000Z\r\nDUE:20260824T110000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nEND:VTODO\r\n"
                    + "BEGIN:VTODO\r\nUID:recurring\r\nRECURRENCE-ID:20260825T100000Z\r\nDTSTAMP:20260823T120000Z\r\nSUMMARY:Changed override\r\nDTSTART:20260825T140000Z\r\nDUE:20260825T150000Z\r\nSTATUS:COMPLETED\r\nCOMPLETED:20260825T150000Z\r\nEND:VTODO\r\n";
            return Encoding.UTF8.GetBytes(
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Todo Module Tests//EN\r\n"
                + component
                + "END:VCALENDAR\r\n");
        }
    }

    private static ReadOnlyMemory<byte> Ics(string component) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Todo Module Tests//EN\r\n"
        + component
        + "END:VCALENDAR\r\n");

}
