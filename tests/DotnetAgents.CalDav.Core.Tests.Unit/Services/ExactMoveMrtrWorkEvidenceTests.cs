using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

public sealed class ExactMoveMrtrWorkEvidenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(600)]
    public async Task ConfirmedMoveUsesTwoRoundsOfConstantHttpWork(
        int destinationCardinality)
    {
        using var handler = new ObservationHandler(destinationCardinality);
        using var httpClient = new HttpClient(handler);
        var options = OptionsForEvidence();
        ICalendarService CreateService() => CreateEvidenceService(httpClient, options);
        var request = ExactMoveRequest();
        var started = Stopwatch.GetTimestamp();

        var result = await ExecuteTwoRoundMrtrAsync(CreateService, request, CancellationToken.None);

        var elapsed = Stopwatch.GetElapsedTime(started);
        result.Code.ShouldBe(CalendarExactResourceCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        var snapshot = result.Snapshot.ShouldNotBeNull();
        snapshot.AuthoritativeUtf8.Span
            .SequenceEqual(ObservationHandler.SourceContent)
            .ShouldBeTrue();
        AssertConstantWorkTrace(handler);
        output.WriteLine(
            "exact-move-mrtr-observation implementation=server-authoritative "
            + $"destination_resources={destinationCardinality} duration_ms={elapsed.TotalMilliseconds:F3} "
            + $"requests={handler.RequestCount} propfind={handler.PropFindCount} report={handler.ReportCount} "
            + $"source_get={handler.SourceGetCount} destination_get={handler.DestinationGetCount} "
            + $"unrelated_get={handler.UnrelatedGetCount} move={handler.MoveCount}");
    }

    [Theory]
    [InlineData(1, "opaque")]
    [InlineData(50, "opaque")]
    [InlineData(600, "opaque")]
    [InlineData(1, "oversized")]
    [InlineData(50, "oversized")]
    [InlineData(600, "oversized")]
    [InlineData(1, "weak-etag")]
    [InlineData(50, "weak-etag")]
    [InlineData(600, "weak-etag")]
    public async Task UnrelatedResourceShapeDoesNotAddRevisionReads(
        int destinationCardinality,
        string unrelatedShape)
    {
        using var handler = new ObservationHandler(destinationCardinality, unrelatedShape);
        using var httpClient = new HttpClient(handler);
        var options = OptionsForEvidence();
        ICalendarService CreateService() => CreateEvidenceService(httpClient, options);
        var request = ExactMoveRequest();

        var result = await ExecuteTwoRoundMrtrAsync(CreateService, request, CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        AssertConstantWorkTrace(handler);
        output.WriteLine(
            $"exact-move-mrtr-shape implementation=server-authoritative shape={unrelatedShape} "
            + $"destination_resources={destinationCardinality} requests={handler.RequestCount} "
            + $"propfind={handler.PropFindCount} report={handler.ReportCount} "
            + $"source_get={handler.SourceGetCount} destination_get={handler.DestinationGetCount} "
            + $"unrelated_get={handler.UnrelatedGetCount} move={handler.MoveCount}");
    }

    private static async Task<CalendarExactResourceResult> ExecuteTwoRoundMrtrAsync(
        Func<ICalendarService> createService,
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken)
    {
        var initialReview = await createService().ReviewExactMoveResourceAsync(request, cancellationToken);
        initialReview.Outcome.ShouldBeNull();
        var binding = initialReview.Binding.ShouldNotBeNull();
        binding.SourceIntentDigest.Length.ShouldBe(SHA256.HashSizeInBytes);
        return await createService().ExecuteConfirmedExactMoveResourceAsync(request, binding, cancellationToken);
    }

    private static void AssertConstantWorkTrace(ObservationHandler handler)
    {
        handler.PropFindCount.ShouldBe(4);
        handler.ReportCount.ShouldBe(0);
        handler.SourceGetCount.ShouldBe(3);
        handler.DestinationGetCount.ShouldBe(3);
        handler.UnrelatedGetCount.ShouldBe(0);
        handler.MoveCount.ShouldBe(1);
        handler.RequestCount.ShouldBe(11);
    }

    private static CalDavOptions OptionsForEvidence() => new()
    {
        BaseUrl = ObservationHandler.CalendarHomeHref,
        Username = "user",
        Password = "secret",
        CalendarHrefs = $"{ObservationHandler.SourceCalendarHref},{ObservationHandler.DestinationCalendarHref}",
        InteroperabilityProfile = "radicale-3.7.8"
    };

    private static ICalendarService CreateEvidenceService(HttpClient httpClient, CalDavOptions options) =>
        new CalendarService(
            new CalDavClient(
                httpClient,
                Options.Create(options),
                Substitute.For<ILogger<CalDavClient>>()),
            Options.Create(options),
            Substitute.For<ILogger<CalendarService>>(),
            TimeProvider.System,
            Substitute.For<ICalendarEntityIdentityGenerator>());

    private static CalendarExactMoveRequest ExactMoveRequest() => new(
        new CalendarResourceRevisionReference(
            ObservationHandler.SourceHref,
            ObservationHandler.EntityUid,
            CalendarEntityKind.Todo,
            "\"r1\""),
        ObservationHandler.DestinationHref);

    private sealed class ObservationHandler : HttpMessageHandler
    {
        internal const string CalendarHomeHref = "https://cal.example/calendars/test/";
        internal const string SourceCalendarHref = CalendarHomeHref + "source/";
        internal const string DestinationCalendarHref = CalendarHomeHref + "destination/";
        internal const string SourceHref = SourceCalendarHref + "reviewed.ics";
        internal const string DestinationHref = DestinationCalendarHref + "moved.ics";
        internal const string EntityUid = "exact-move-evidence";
        internal static readonly byte[] SourceContent = Todo(EntityUid, "Exact move evidence");
        private static readonly byte[] OversizedContent = new byte[(4 * 1024 * 1024) + 1];
        private readonly IReadOnlySet<string> _unrelatedHrefs;
        private readonly string _unrelatedShape;
        private int _requestCount;
        private int _propFindCount;
        private int _reportCount;
        private int _sourceGetCount;
        private int _destinationGetCount;
        private int _unrelatedGetCount;
        private int _moveCount;
        private int _moved;

        internal ObservationHandler(int destinationCardinality, string unrelatedShape = "ordinary")
        {
            _unrelatedHrefs = Enumerable.Range(0, destinationCardinality)
                .Select(index => DestinationCalendarHref + $"unrelated-{index}.ics")
                .ToHashSet(StringComparer.Ordinal);
            _unrelatedShape = unrelatedShape;
        }

        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal int PropFindCount => Volatile.Read(ref _propFindCount);

        internal int ReportCount => Volatile.Read(ref _reportCount);

        internal int SourceGetCount => Volatile.Read(ref _sourceGetCount);

        internal int DestinationGetCount => Volatile.Read(ref _destinationGetCount);

        internal int UnrelatedGetCount => Volatile.Read(ref _unrelatedGetCount);

        internal int MoveCount => Volatile.Read(ref _moveCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
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
            Interlocked.Increment(ref _propFindCount);
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
            Interlocked.Increment(ref _reportCount);
            return Xml(string.Concat(_unrelatedHrefs.Select(href =>
                $"<d:response><d:href>{href}</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response>")));
        }

        private HttpResponseMessage Read(string href)
        {
            if (href == SourceHref)
            {
                Interlocked.Increment(ref _sourceGetCount);
                return Volatile.Read(ref _moved) == 0
                    ? CalendarContent(SourceContent, "\"r1\"")
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (href == DestinationHref)
            {
                Interlocked.Increment(ref _destinationGetCount);
                return Volatile.Read(ref _moved) == 0
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : CalendarContent(SourceContent, "\"r2\"");
            }
            if (_unrelatedHrefs.Contains(href))
            {
                Interlocked.Increment(ref _unrelatedGetCount);
                return UnrelatedContent(href);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private HttpResponseMessage UnrelatedContent(string href)
        {
            var index = href[(href.LastIndexOf('-') + 1)..^4];
            return _unrelatedShape switch
            {
                "opaque" => CalendarContent("not-a-calendar"u8.ToArray(), "\"unrelated\""),
                "oversized" => CalendarContent(OversizedContent, "\"unrelated\""),
                "weak-etag" => CalendarContent(
                    Todo($"unrelated-{index}", $"Unrelated {index}"),
                    "\"unrelated\"",
                    isWeak: true),
                _ => CalendarContent(Todo($"unrelated-{index}", $"Unrelated {index}"), "\"unrelated\"")
            };
        }

        private HttpResponseMessage Move(HttpRequestMessage request, string href)
        {
            Interlocked.Increment(ref _moveCount);
            href.ShouldBe(SourceHref);
            request.Headers.GetValues("Destination").Single().ShouldBe(DestinationHref);
            request.Headers.GetValues("Overwrite").Single().ShouldBe("F");
            request.Headers.IfMatch.Single().ToString().ShouldBe("\"r1\"");
            Volatile.Write(ref _moved, 1);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        private static HttpResponseMessage CalendarContent(
            byte[] content,
            string entityTag,
            bool isWeak = false)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
            response.Headers.ETag = new EntityTagHeaderValue(entityTag, isWeak);
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
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Move Observation//EN\r\n"
            + $"BEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260823T120000Z\r\n"
            + $"SUMMARY:{summary}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
    }
}
