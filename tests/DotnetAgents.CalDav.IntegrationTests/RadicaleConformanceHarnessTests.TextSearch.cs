using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

public sealed partial class RadicaleConformanceHarnessTests
{
    private const int MatchingEventCount = 36;
    private const int DecoyEventCount = 24;
    private static readonly XNamespace TextSearchDav = "DAV:";
    private static readonly XNamespace TextSearchCalDav = "urn:ietf:params:xml:ns:caldav";

    [Fact]
    public async Task Pinned_profile_text_match_reduces_multi_page_queries_without_changing_local_truth()
    {
        using var client = CreateProbeClient();
        var calendar = new Uri(fixture.BaseUrl + "/conformance/text-search/", UriKind.Absolute);
        (await CreateCalendarAsync(client, calendar, "Text Search", "VEVENT", "VTODO")).ShouldBe(HttpStatusCode.Created);
        var corpus = TextSearchCorpus();
        foreach (var (name, content) in corpus)
        {
            (await SendProbeAsync(client, HttpMethod.Put, new Uri(calendar, name), content, ("If-None-Match", "*")))
                .Status.ShouldBe(HttpStatusCode.Created);
        }
        var reports = new ConcurrentQueue<string>();
        await using var provider = CreateTextSearchProvider(calendar.AbsoluteUri, reports);
        var module = provider.GetRequiredService<ICalendarQueryModule>();

        var pages = await ReadAllEntityPagesAsync(module, reports, new CalendarTextFilter("DENTIST"), pageSize: 20);
        var matchingUids = pages.SelectMany(page => page.Value.Items)
            .Select(item => item.Value.GetProperty("projection").GetProperty("uid").GetString())
            .ToArray();

        pages.Count.ShouldBe(2);
        matchingUids.Order(StringComparer.Ordinal).ShouldBe(Enumerable.Range(0, MatchingEventCount)
            .Select(index => $"dentist-{index:D2}")
            .Append("series")
            .Order(StringComparer.Ordinal));
        var textReports = reports.Where(body => body.Contains("text-match", StringComparison.Ordinal)).ToArray();
        textReports.Select(body => XDocument.Parse(body).Descendants(TextSearchCalDav + "prop-filter").Single()
                .Attribute("name")!.Value)
            .ShouldBe(["SUMMARY", "DESCRIPTION", "LOCATION", "CATEGORIES"]);
        MultigetHrefCount(reports).ShouldBe(MatchingEventCount + 1);

        (await QueryEntityUidsAsync(module, new CalendarTextFilter(Categories: ["health"])))
            .Length.ShouldBe(MatchingEventCount / 4 + DecoyEventCount / 2);
        (await QueryEntityUidsAsync(module, new CalendarTextFilter("dentist", ["HEALTH"])))
            .ShouldBe(Enumerable.Range(0, MatchingEventCount).Where(index => index % 4 == 3)
                .Select(index => $"dentist-{index:D2}"));
        (await QueryEntityUidsAsync(module, new CalendarTextFilter("odontológica"))).ShouldBe(["accent"]);

        var dentistOccurrences = await QueryTextOccurrencesAsync(module, new CalendarTextFilter("dentist"));
        var planningOccurrences = await QueryTextOccurrencesAsync(module, new CalendarTextFilter("weekly planning"));
        dentistOccurrences.ShouldBe(["2026-11-03T09:00:00Z"]);
        planningOccurrences.ShouldBe([
            "2026-11-02T09:00:00Z",
            "2026-11-04T09:00:00Z",
            "2026-11-05T09:00:00Z",
            "2026-11-06T09:00:00Z"
        ]);

        var todos = (await module.QueryTodosAsync(
            new CalendarTodoQueryRequest.Start(
                new CalendarTodoQuery(
                    CalendarEntityScope.All,
                    EvaluationTimeZone: "UTC",
                    TextFilter: new CalendarTextFilter("dentist")),
                [CalendarTodoProjectionField.Summary]),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarTodoQueryPageItem>.Page>();
        todos.Value.Items.Select(item => item.Value.GetProperty("summary").GetString())
            .ShouldBe(["Call dentist 0", "Call dentist 1", "Call dentist 2"]);
    }

    private static async Task<List<QueryReply<CalendarEntityQueryItem>.Page>> ReadAllEntityPagesAsync(
        ICalendarQueryModule module,
        ConcurrentQueue<string> reports,
        CalendarTextFilter filter,
        int pageSize)
    {
        var pages = new List<QueryReply<CalendarEntityQueryItem>.Page>
        {
            (await module.QueryEntitiesAsync(
                new CalendarEntityQueryRequest.Start(
                    new CalendarEntityQuery(CalendarEntityScope.All, [CalendarEntityKind.Event], TextFilter: filter),
                    pageSize),
                TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>()
        };
        var startReports = reports.Count;
        while (pages[^1].Value.NextCursor is { } cursor)
        {
            pages.Add((await module.QueryEntitiesAsync(
                new CalendarEntityQueryRequest.Continue(cursor, pageSize),
                TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>());
        }
        reports.Count.ShouldBe(startReports);
        return pages;
    }

    private static async Task<string[]> QueryEntityUidsAsync(ICalendarQueryModule module, CalendarTextFilter filter) =>
        (await module.QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(
                new CalendarEntityQuery(CalendarEntityScope.All, [CalendarEntityKind.Event], TextFilter: filter),
                200),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>()
        .Value.Items.Select(item => item.Value.GetProperty("projection").GetProperty("uid").GetString()!)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static async Task<string[]> QueryTextOccurrencesAsync(ICalendarQueryModule module, CalendarTextFilter filter) =>
        (await module.QueryOccurrencesAsync(
            new CalendarOccurrenceQueryRequest.Start(
                new CalendarOccurrenceQuery(
                    CalendarEntityScope.All,
                    DateTimeOffset.Parse("2026-11-01T00:00:00Z", CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse("2026-12-01T00:00:00Z", CultureInfo.InvariantCulture),
                    "UTC",
                    TextFilter: filter),
                200),
            TestContext.Current.CancellationToken)).ShouldBeOfType<QueryReply<CalendarOccurrenceQueryItem>.Page>()
        .Value.Items.Select(item => item.Value.GetProperty("recurrenceIdentity").GetProperty("value")
            .GetProperty("value").GetString()!)
        .ToArray();

    private static int MultigetHrefCount(IEnumerable<string> reports) => reports
        .Where(body => body.Contains("calendar-multiget", StringComparison.Ordinal))
        .Sum(body => XDocument.Parse(body).Descendants(TextSearchDav + "href").Count());

    private ServiceProvider CreateTextSearchProvider(string calendarHref, ConcurrentQueue<string> reports)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = fixture.BaseUrl;
            options.CalendarHrefs = calendarHref;
            options.Username = ConformanceUsername;
            options.Password = ConformancePassword;
        });
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new ReportCaptureFilter(reports));
        return services.BuildServiceProvider();
    }

    private static IReadOnlyList<(string Name, string Content)> TextSearchCorpus()
    {
        var resources = new List<(string Name, string Content)>();
        for (var index = 0; index < MatchingEventCount; index++)
        {
            var lines = (index % 4) switch
            {
                0 => $"SUMMARY:Dentist visit {index}\r\n",
                1 => $"SUMMARY:Visit {index}\r\nDESCRIPTION:Bring the DENTIST forms\\, please\r\n",
                2 => $"SUMMARY:Visit {index}\r\nLOCATION:Downtown dentist clinic\r\n",
                _ => $"SUMMARY:Visit {index}\r\nCATEGORIES:Health,Dentist appointments\r\n"
            };
            resources.Add(($"dentist-{index:D2}.ics", TextSearchEvent($"dentist-{index:D2}", 1 + index % 28, lines)));
        }
        for (var index = 0; index < DecoyEventCount; index++)
        {
            var categories = index % 2 == 0 ? "CATEGORIES:Health\r\n" : string.Empty;
            resources.Add(($"decoy-{index:D2}.ics", TextSearchEvent(
                $"decoy-{index:D2}",
                1 + index % 28,
                $"SUMMARY:Team sync {index}\r\nDESCRIPTION:d-entist is not a match\r\n{categories}")));
        }
        resources.Add(("accent.ics", TextSearchEvent("accent", 20, "SUMMARY:Consulta odontológica\r\n")));
        resources.Add(("series.ics",
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:series\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20261102T090000Z\r\n"
            + "DURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=5\r\nSUMMARY:Weekly planning\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:series\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20261103T090000Z\r\n"
            + "DTSTART:20261103T150000Z\r\nDURATION:PT1H\r\nSUMMARY:Planning with dentist\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n"));
        for (var index = 0; index < 5; index++)
        {
            var summary = index < 3 ? $"Call dentist {index}" : $"Buy milk {index}";
            resources.Add(($"todo-{index}.ics",
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\nBEGIN:VTODO\r\n"
                + $"UID:todo-{index}\r\nDTSTAMP:20260815T120000Z\r\nDUE:202610{15 + index}T090000Z\r\n"
                + $"SUMMARY:{summary}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n"));
        }
        return resources;
    }

    private static string TextSearchEvent(string uid, int day, string lines) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\nBEGIN:VEVENT\r\n"
        + $"UID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:202610{day:D2}T100000Z\r\nDURATION:PT1H\r\n"
        + $"{lines}END:VEVENT\r\nEND:VCALENDAR\r\n";

    private sealed class ReportCaptureFilter(ConcurrentQueue<string> reports) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.AdditionalHandlers.Insert(0, new ReportCaptureHandler(reports));
        };
    }

    private sealed class ReportCaptureHandler(ConcurrentQueue<string> reports) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method.Method == "REPORT" && request.Content is not null)
                reports.Enqueue(await request.Content.ReadAsStringAsync(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
