using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

[Collection("RadicaleConformanceCollection")]
public sealed class SemanticMoveRadicaleSizeEvidenceTests(
    RadicaleConformanceFixture fixture,
    ITestOutputHelper output)
{
    private static readonly int[] CorpusSizes = [1, 50, 600];

    [Fact]
    public async Task PinnedRadicaleObservesMoveWorkAtOneFiftyAndSixHundredResources()
    {
        using var probe = CreateProbeClient();
        var sourceCalendar = new Uri(fixture.BaseUrl + "/conformance/move-work-source/", UriKind.Absolute);
        var destinationCalendar = new Uri(fixture.BaseUrl + "/conformance/move-work-destination/", UriKind.Absolute);
        var sourceCreated = false;
        var destinationCreated = false;
        try
        {
            (await CreateCalendarAsync(probe, sourceCalendar, "Move Work Source")).ShouldBe(HttpStatusCode.Created);
            sourceCreated = true;
            (await CreateCalendarAsync(probe, destinationCalendar, "Move Work Destination"))
                .ShouldBe(HttpStatusCode.Created);
            destinationCreated = true;
            var seeded = 0;
            foreach (var destinationCardinality in CorpusSizes)
            {
                while (seeded < destinationCardinality)
                {
                    var response = await SendAsync(
                        probe,
                        HttpMethod.Put,
                        new Uri(destinationCalendar, $"unrelated-{seeded}.ics"),
                        UnrelatedTodo(seeded),
                        ("If-None-Match", "*"));
                    response.Status.ShouldBe(HttpStatusCode.Created);
                    seeded++;
                }
                (await CountTodosAsync(probe, destinationCalendar)).ShouldBe(destinationCardinality);
                var uid = $"pinned-move-work-{destinationCardinality}";
                var source = await PutResourceAsync(
                    probe,
                    new Uri(sourceCalendar, $"reviewed-{destinationCardinality}.ics"),
                    Todo(uid));
                var destinationHref = BuildDestinationHref(destinationCalendar, uid);
                var wire = new MoveWorkTraceFilter(source.Href, destinationCalendar, destinationHref);
                await using var provider = CreateProvider(
                    fixture.BaseUrl,
                    $"{sourceCalendar.AbsoluteUri},{destinationCalendar.AbsoluteUri}",
                    wire);
                var started = Stopwatch.GetTimestamp();

                var result = await provider.GetRequiredService<ICalendarService>().MoveResourceAsync(
                    new CalendarResourceMoveRequest(
                        new CalendarResourceRevisionReference(
                            source.Href.AbsoluteUri,
                            uid,
                            CalendarEntityKind.Todo,
                            source.EntityTag),
                        CalendarMoveDestination.Selected(new CalendarReference(
                            Href: destinationCalendar.AbsoluteUri))),
                    TestContext.Current.CancellationToken);

                var elapsed = Stopwatch.GetElapsedTime(started);
                result.Code.ShouldBe(CalendarResourceMoveCode.Success);
                result.MutationState.ShouldBe(CalendarMutationState.Committed);
                var mode = ResolveEvidenceMode(Environment.GetEnvironmentVariable("CALDAV_MOVE_EVIDENCE_MODE"));
                wire.PropFindCount.ShouldBe(5);
                wire.SourceGetCount.ShouldBe(2);
                wire.DestinationGetCount.ShouldBe(2);
                wire.MoveCount.ShouldBe(1);
                wire.MultigetCount.ShouldBe(0);
                if (mode == "legacy-scan")
                {
                    wire.ReportCount.ShouldBe(2);
                    wire.UnrelatedGetCount.ShouldBe(destinationCardinality);
                    wire.RequestCount.ShouldBe(destinationCardinality + 12);
                }
                else
                {
                    wire.ReportCount.ShouldBe(0);
                    wire.UnrelatedGetCount.ShouldBe(0);
                    wire.RequestCount.ShouldBe(10);
                }
                foreach (var index in new[] { 0, destinationCardinality / 2, destinationCardinality - 1 }.Distinct())
                {
                    var observed = await SendAsync(
                        probe,
                        HttpMethod.Get,
                        new Uri(destinationCalendar, $"unrelated-{index}.ics"));
                    observed.Status.ShouldBe(HttpStatusCode.OK);
                    observed.EntityTag.ShouldNotBeNull();
                }
                var moved = result.Snapshot.ShouldNotBeNull();
                (await SendAsync(
                    probe,
                    HttpMethod.Delete,
                    new Uri(moved.ResourceHref, UriKind.Absolute),
                    content: null,
                    ("If-Match", moved.EntityTag))).Status.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Evidence = "CAL-EVIDENCE-013",
                    Implementation = mode,
                    DestinationResources = destinationCardinality,
                    DurationMilliseconds = elapsed.TotalMilliseconds,
                    Requests = wire.RequestCount,
                    PropFind = wire.PropFindCount,
                    Report = wire.ReportCount,
                    Multiget = wire.MultigetCount,
                    SourceGets = wire.SourceGetCount,
                    DestinationGets = wire.DestinationGetCount,
                    UnrelatedGets = wire.UnrelatedGetCount,
                    Move = wire.MoveCount,
                    fixture.Runtime.IndexDigest,
                    fixture.Runtime.ResolvedPlatformManifestDigest,
                    fixture.Runtime.RuntimeArchitecture,
                    fixture.Runtime.RadicaleVersion,
                    fixture.Runtime.Variant,
                    fixture.Runtime.ConfiguredTimeZone,
                    fixture.Runtime.StrictPreconditions,
                    fixture.Runtime.PythonVersion,
                    fixture.Runtime.VobjectVersion,
                    fixture.Runtime.RuntimeTimeZone
                }));
            }
        }
        finally
        {
            if (destinationCreated)
            {
                (await SendAsync(probe, HttpMethod.Delete, destinationCalendar)).Status
                    .ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
            }
            if (sourceCreated)
            {
                (await SendAsync(probe, HttpMethod.Delete, sourceCalendar)).Status
                    .ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
            }
        }
    }

    private static ServiceProvider CreateProvider(
        string baseUrl,
        string calendarHrefs,
        MoveWorkTraceFilter wire)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = baseUrl;
            options.CalendarHrefs = calendarHrefs;
            options.Username = RadicaleConformanceFixture.Username;
            options.Password = RadicaleConformanceFixture.Password;
            options.GetType().GetProperty("InteroperabilityProfile")?.SetValue(options, "radicale-3.7.8");
        });
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(wire);
        return services.BuildServiceProvider();
    }

    private static HttpClient CreateProbeClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero
        });
        client.DefaultRequestVersion = HttpVersion.Version10;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        client.DefaultRequestHeaders.ConnectionClose = true;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{RadicaleConformanceFixture.Username}:{RadicaleConformanceFixture.Password}")));
        return client;
    }

    private static async Task<HttpStatusCode> CreateCalendarAsync(HttpClient client, Uri calendar, string name)
    {
        var body = "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
            + "<D:mkcol xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\"><D:set><D:prop>"
            + "<D:resourcetype><D:collection/><C:calendar/></D:resourcetype>"
            + $"<D:displayname>{name}</D:displayname>"
            + "<C:supported-calendar-component-set><C:comp name=\"VTODO\"/>"
            + "</C:supported-calendar-component-set></D:prop></D:set></D:mkcol>";
        return (await SendAsync(client, new HttpMethod("MKCOL"), calendar, body)).Status;
    }

    private static async Task<int> CountTodosAsync(HttpClient client, Uri calendar)
    {
        const string body = "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
            + "<C:calendar-query xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\">"
            + "<D:prop><D:getetag/></D:prop><C:filter><C:comp-filter name=\"VCALENDAR\">"
            + "<C:comp-filter name=\"VTODO\"/></C:comp-filter></C:filter></C:calendar-query>";
        var response = await SendAsync(client, new HttpMethod("REPORT"), calendar, body, ("Depth", "1"));
        response.Status.ShouldBe(HttpStatusCode.MultiStatus);
        return XDocument.Parse(response.Body).Descendants()
            .Count(element => element.Name.LocalName == "response");
    }

    private static async Task<SeededResource> PutResourceAsync(HttpClient client, Uri href, string content)
    {
        (await SendAsync(client, HttpMethod.Put, href, content, ("If-None-Match", "*"))).Status
            .ShouldBe(HttpStatusCode.Created);
        var observed = await SendAsync(client, HttpMethod.Get, href);
        observed.Status.ShouldBe(HttpStatusCode.OK);
        return new SeededResource(href, observed.EntityTag.ShouldNotBeNull());
    }

    private static async Task<ProbeResponse> SendAsync(
        HttpClient client,
        HttpMethod method,
        Uri href,
        string? content = null,
        params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, href);
        if (content is not null)
        {
            request.Content = new StringContent(
                content,
                Encoding.UTF8,
                method == HttpMethod.Put ? "text/calendar" : "application/xml");
        }
        foreach (var header in headers)
            request.Headers.TryAddWithoutValidation(header.Name, header.Value).ShouldBeTrue();
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        var body = response.StatusCode == HttpStatusCode.MultiStatus
            ? await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            : string.Empty;
        return new ProbeResponse(response.StatusCode, response.Headers.ETag?.ToString(), body);
    }

    private static Uri BuildDestinationHref(Uri calendar, string uid)
    {
        var opaqueName = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(uid)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new Uri(calendar, $"{opaqueName}.ics");
    }

    private static string Todo(string uid) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Move Evidence//EN\r\nBEGIN:VTODO\r\n"
        + $"UID:{uid}\r\nDTSTAMP:20260823T120000Z\r\nSUMMARY:move evidence\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private static string UnrelatedTodo(int index) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Move Evidence//EN\r\nBEGIN:VTODO\r\n"
        + $"UID:unrelated-{index}\r\nDTSTAMP:20260823T120000Z\r\nSUMMARY:unrelated {index}\r\n"
        + $"X-PRIVATE-{index}:opaque-value-{index}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private static string ResolveEvidenceMode(string? configured) => configured switch
    {
        null or "" => "server-authoritative",
        "legacy-scan" or "server-authoritative" => configured,
        _ => throw new ArgumentException("CALDAV_MOVE_EVIDENCE_MODE is not recognized.", nameof(configured))
    };

    private sealed record SeededResource(Uri Href, string EntityTag);

    private sealed record ProbeResponse(HttpStatusCode Status, string? EntityTag, string Body);

    private sealed class MoveWorkTraceFilter(
        Uri sourceHref,
        Uri destinationCalendar,
        Uri destinationHref) : IHttpMessageHandlerBuilderFilter
    {
        private readonly Uri _sourceHref = sourceHref;
        private readonly Uri _destinationCalendar = destinationCalendar;
        private readonly Uri _destinationHref = destinationHref;
        internal int RequestCount;
        internal int PropFindCount;
        internal int ReportCount;
        internal int MultigetCount;
        internal int SourceGetCount;
        internal int DestinationGetCount;
        internal int UnrelatedGetCount;
        internal int MoveCount;

        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.AdditionalHandlers.Insert(0, new TraceHandler(this));
        };

        private sealed class TraceHandler(MoveWorkTraceFilter owner) : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.RequestCount);
                switch (request.Method.Method)
                {
                    case "PROPFIND":
                        Interlocked.Increment(ref owner.PropFindCount);
                        break;
                    case "REPORT":
                        Interlocked.Increment(ref owner.ReportCount);
                        if (request.Content is not null
                            && (await request.Content.ReadAsStringAsync(cancellationToken))
                            .Contains("calendar-multiget", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref owner.MultigetCount);
                        }
                        break;
                    case "MOVE":
                        Interlocked.Increment(ref owner.MoveCount);
                        break;
                    case "GET" when request.RequestUri == owner._sourceHref:
                        Interlocked.Increment(ref owner.SourceGetCount);
                        break;
                    case "GET" when request.RequestUri == owner._destinationHref:
                        Interlocked.Increment(ref owner.DestinationGetCount);
                        break;
                    case "GET" when owner._destinationCalendar.IsBaseOf(request.RequestUri!):
                        Interlocked.Increment(ref owner.UnrelatedGetCount);
                        break;
                }
                return await base.SendAsync(request, cancellationToken);
            }
        }
    }
}
