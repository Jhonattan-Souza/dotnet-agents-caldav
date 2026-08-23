using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class SemanticMoveWorkEvidenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(600)]
    public async Task SameHttpCorpusObservesBaselineScanAndChangedConstantWork(int destinationCardinality)
    {
        using var handler = new ObservationHandler(destinationCardinality);
        var options = new CalDavOptions
        {
            BaseUrl = ObservationHandler.CalendarHomeHref,
            Username = "user",
            Password = "secret",
            CalendarHrefs = $"{ObservationHandler.SourceCalendarHref},{ObservationHandler.DestinationCalendarHref}"
        };
        var profileProperty = typeof(CalDavOptions).GetProperty("InteroperabilityProfile");
        profileProperty?.SetValue(options, "radicale-3.7.8");
        var evidenceMode = ResolveEvidenceMode(Environment.GetEnvironmentVariable("CALDAV_MOVE_EVIDENCE_MODE"));
        using var httpClient = new HttpClient(handler);
        var client = new CalDavClient(
            httpClient,
            Options.Create(options),
            Substitute.For<ILogger<CalDavClient>>());
        var service = new CalendarService(
            client,
            Options.Create(options),
            Substitute.For<ILogger<CalendarService>>(),
            TimeProvider.System,
            Substitute.For<ICalendarEntityIdentityGenerator>());
        var started = Stopwatch.GetTimestamp();

        var result = await service.MoveResourceAsync(
            new CalendarResourceMoveRequest(
                new CalendarResourceRevisionReference(
                    ObservationHandler.SourceHref,
                    "reviewed-move",
                    CalendarEntityKind.Todo,
                    "\"r1\""),
                CalendarMoveDestination.Selected(new CalendarReference(
                    Href: ObservationHandler.DestinationCalendarHref))),
            CancellationToken.None);

        var elapsed = Stopwatch.GetElapsedTime(started);
        result.Code.ShouldBe(CalendarResourceMoveCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        handler.PropFindCount.ShouldBe(2);
        handler.SourceGetCount.ShouldBe(2);
        handler.DestinationGetCount.ShouldBe(2);
        handler.MoveCount.ShouldBe(1);
        if (evidenceMode == "legacy-scan")
        {
            handler.ReportCount.ShouldBe(2);
            handler.UnrelatedGetCount.ShouldBe(destinationCardinality);
            handler.GetCount.ShouldBe(destinationCardinality + 4);
            handler.RequestCount.ShouldBe(destinationCardinality + 9);
        }
        else
        {
            handler.ReportCount.ShouldBe(0);
            handler.UnrelatedGetCount.ShouldBe(0);
            handler.GetCount.ShouldBe(4);
            handler.RequestCount.ShouldBe(7);
        }
        output.WriteLine(
            $"semantic-move-observation implementation={evidenceMode} "
            + $"destination_resources={destinationCardinality} duration_ms={elapsed.TotalMilliseconds:F3} "
            + $"requests={handler.RequestCount} propfind={handler.PropFindCount} report={handler.ReportCount} "
            + $"get={handler.GetCount} unrelated_get={handler.UnrelatedGetCount} move={handler.MoveCount}");
    }

    [Fact]
    public void EvidenceModeRejectsUnknownValues() => Should.Throw<ArgumentException>(() =>
        ResolveEvidenceMode("unknown"));

    private static string ResolveEvidenceMode(string? configured) => configured switch
    {
        null or "" => "server-authoritative",
        "legacy-scan" or "server-authoritative" => configured,
        _ => throw new ArgumentException("CALDAV_MOVE_EVIDENCE_MODE is not recognized.", nameof(configured))
    };

    private sealed class ObservationHandler : HttpMessageHandler
    {
        internal const string CalendarHomeHref = "https://cal.example/calendars/test/";
        internal const string SourceCalendarHref = CalendarHomeHref + "source/";
        internal const string DestinationCalendarHref = CalendarHomeHref + "destination/";
        internal const string SourceHref = SourceCalendarHref + "reviewed.ics";
        private static readonly string DestinationHref = CalendarResourceCreateProtocol.BuildResourceHref(
            DestinationCalendarHref,
            "reviewed-move");
        private static readonly byte[] MovedContent = Todo("reviewed-move", "Moved resource");
        private readonly IReadOnlyDictionary<string, byte[]> _unrelatedResources;
        private int _moved;

        internal ObservationHandler(int destinationCardinality)
        {
            _unrelatedResources = Enumerable.Range(0, destinationCardinality).ToDictionary(
                index => DestinationCalendarHref + $"unrelated-{index}.ics",
                index => Todo($"unrelated-{index}", $"Unrelated {index}"),
                StringComparer.Ordinal);
        }

        internal int RequestCount { get; private set; }

        internal int PropFindCount { get; private set; }

        internal int ReportCount { get; private set; }

        internal int GetCount { get; private set; }

        internal int SourceGetCount { get; private set; }

        internal int DestinationGetCount { get; private set; }

        internal int UnrelatedGetCount { get; private set; }

        internal int MoveCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var href = request.RequestUri!.AbsoluteUri;
            if (request.Method.Method == "PROPFIND")
                return Task.FromResult(Discovery(request));
            if (request.Method.Method == "REPORT")
                return Task.FromResult(Report());
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Read(href));
            if (request.Method.Method == "MOVE")
                return Task.FromResult(Move(request, href));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        }

        private HttpResponseMessage Discovery(HttpRequestMessage request)
        {
            PropFindCount++;
            var depth = request.Headers.GetValues("Depth").Single();
            var body = depth == "0"
                ? $"<d:response><d:href>{CalendarHomeHref}</d:href><d:propstat><d:prop>"
                    + $"<c:calendar-home-set><d:href>{CalendarHomeHref}</d:href></c:calendar-home-set>"
                    + "</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                : Calendar(SourceCalendarHref, "Source") + Calendar(DestinationCalendarHref, "Destination");
            return Xml(body);
        }

        private HttpResponseMessage Report()
        {
            ReportCount++;
            return Xml(string.Concat(_unrelatedResources.Keys.Select(href =>
                $"<d:response><d:href>{href}</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response>")));
        }

        private HttpResponseMessage Read(string href)
        {
            GetCount++;
            if (href == SourceHref)
            {
                SourceGetCount++;
                return Volatile.Read(ref _moved) == 0
                    ? CalendarContent(MovedContent, "\"r1\"")
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (href == DestinationHref)
            {
                DestinationGetCount++;
                return Volatile.Read(ref _moved) == 0
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : CalendarContent(MovedContent, "\"r2\"");
            }
            if (_unrelatedResources.TryGetValue(href, out var content))
            {
                UnrelatedGetCount++;
                return CalendarContent(content, "\"unrelated\"");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private HttpResponseMessage Move(HttpRequestMessage request, string href)
        {
            MoveCount++;
            href.ShouldBe(SourceHref);
            request.Headers.GetValues("Destination").Single().ShouldBe(DestinationHref);
            request.Headers.GetValues("Overwrite").Single().ShouldBe("F");
            request.Headers.IfMatch.Single().ToString().ShouldBe("\"r1\"");
            Volatile.Write(ref _moved, 1);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        private static HttpResponseMessage CalendarContent(byte[] content, string entityTag)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
            response.Headers.ETag = new EntityTagHeaderValue(entityTag);
            return response;
        }

        private static HttpResponseMessage Xml(string responses) => new(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + "<d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
                + responses
                + "</d:multistatus>",
                Encoding.UTF8,
                "application/xml")
        };

        private static string Calendar(string href, string displayName) =>
            $"<d:response><d:href>{href}</d:href><d:propstat><d:prop>"
            + "<d:resourcetype><c:calendar/></d:resourcetype>"
            + $"<d:displayname>{displayName}</d:displayname>"
            + "<c:supported-calendar-component-set><c:comp name=\"VTODO\"/>"
            + "</c:supported-calendar-component-set></d:prop>"
            + "<d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

        private static byte[] Todo(string uid, string summary) => Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Move Observation//EN\r\n"
            + $"BEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260823T120000Z\r\n"
            + $"SUMMARY:{summary}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
    }
}
