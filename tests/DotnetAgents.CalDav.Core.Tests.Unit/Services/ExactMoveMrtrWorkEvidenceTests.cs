using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
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
    public async Task SameHttpCorpusObservesLegacyThreePreparationsAndChangedTwoRoundConstantWork(
        int destinationCardinality)
    {
        using var handler = new ObservationHandler(destinationCardinality);
        using var httpClient = new HttpClient(handler);
        var options = new CalDavOptions
        {
            BaseUrl = ObservationHandler.CalendarHomeHref,
            Username = "user",
            Password = "secret",
            CalendarHrefs = $"{ObservationHandler.SourceCalendarHref},{ObservationHandler.DestinationCalendarHref}"
        };
        options.GetType().GetProperty("InteroperabilityProfile")?.SetValue(options, "radicale-3.7.8");
        var mode = ResolveEvidenceMode(Environment.GetEnvironmentVariable("CALDAV_MOVE_EVIDENCE_MODE"));
        ICalendarService CreateService() => new CalendarService(
            new CalDavClient(
                httpClient,
                Options.Create(options),
                Substitute.For<ILogger<CalDavClient>>()),
            Options.Create(options),
            Substitute.For<ILogger<CalendarService>>(),
            TimeProvider.System,
            Substitute.For<ICalendarEntityIdentityGenerator>());
        var request = new CalendarExactMoveRequest(
            new CalendarResourceRevisionReference(
                ObservationHandler.SourceHref,
                ObservationHandler.EntityUid,
                CalendarEntityKind.Todo,
                "\"r1\""),
            ObservationHandler.DestinationHref);
        var started = Stopwatch.GetTimestamp();

        var result = await ExecuteTwoRoundMrtrAsync(CreateService, request, mode, CancellationToken.None);

        var elapsed = Stopwatch.GetElapsedTime(started);
        Property(result, "Code").ToString().ShouldBe("Success");
        Property(result, "MutationState").ToString().ShouldBe("Committed");
        var snapshot = Property(result, "Snapshot");
        ((ReadOnlyMemory<byte>)Property(snapshot, "AuthoritativeUtf8")).Span
            .SequenceEqual(ObservationHandler.SourceContent)
            .ShouldBeTrue();
        handler.PropFindCount.ShouldBe(4);
        handler.SourceGetCount.ShouldBe(mode == "legacy-scan" ? 4 : 3);
        handler.DestinationGetCount.ShouldBe(mode == "legacy-scan" ? 4 : 3);
        handler.MoveCount.ShouldBe(1);
        if (mode == "legacy-scan")
        {
            handler.ReportCount.ShouldBe(6);
            handler.UnrelatedGetCount.ShouldBe(destinationCardinality * 3);
            handler.RequestCount.ShouldBe((destinationCardinality * 3) + 19);
        }
        else
        {
            handler.ReportCount.ShouldBe(0);
            handler.UnrelatedGetCount.ShouldBe(0);
            handler.RequestCount.ShouldBe(11);
        }
        output.WriteLine(
            $"exact-move-mrtr-observation implementation={mode} "
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
    public async Task UnrelatedResourceShapeCannotAddChangedRevisionReads(
        int destinationCardinality,
        string unrelatedShape)
    {
        using var handler = new ObservationHandler(destinationCardinality, unrelatedShape);
        using var httpClient = new HttpClient(handler);
        var options = OptionsForEvidence();
        var mode = ResolveEvidenceMode(Environment.GetEnvironmentVariable("CALDAV_MOVE_EVIDENCE_MODE"));
        ICalendarService CreateService() => CreateEvidenceService(httpClient, options);
        var request = ExactMoveRequest();

        var result = await ExecuteShapeScenarioAsync(
            CreateService,
            request,
            mode,
            unrelatedShape,
            CancellationToken.None);

        if (mode == "server-authoritative" || unrelatedShape == "opaque")
        {
            Property(result, "Code").ToString().ShouldBe("Success");
            Property(result, "MutationState").ToString().ShouldBe("Committed");
        }
        else
        {
            Property(result, "Code").ToString().ShouldBe(
                unrelatedShape == "oversized" ? "PayloadTooLarge" : "ConcurrencyUnavailable");
            Property(result, "MutationState").ToString().ShouldBe("NotAttempted");
        }
        AssertShapeTrace(handler, mode, unrelatedShape, destinationCardinality);
        output.WriteLine(
            $"exact-move-mrtr-shape implementation={mode} shape={unrelatedShape} "
            + $"destination_resources={destinationCardinality} requests={handler.RequestCount} "
            + $"propfind={handler.PropFindCount} report={handler.ReportCount} "
            + $"source_get={handler.SourceGetCount} destination_get={handler.DestinationGetCount} "
            + $"unrelated_get={handler.UnrelatedGetCount} move={handler.MoveCount}");
    }

    [Fact]
    public void EvidenceModeRejectsUnknownValues() => Should.Throw<ArgumentException>(() =>
        ResolveEvidenceMode("unknown"));

    private static async Task<object> ExecuteTwoRoundMrtrAsync(
        Func<ICalendarService> createService,
        CalendarExactMoveRequest request,
        string mode,
        CancellationToken cancellationToken)
    {
        var initialReview = await InvokeAsync(
            createService(),
            "ReviewExactMoveResourceAsync",
            request,
            cancellationToken);
        OptionalProperty(initialReview, "Outcome").ShouldBeNull();

        if (mode == "server-authoritative")
        {
            var binding = Property(initialReview, "Binding");
            var digest = (ReadOnlyMemory<byte>)Property(binding, "SourceIntentDigest");
            digest.Length.ShouldBe(SHA256.HashSizeInBytes);
            return await InvokeAsync(
                createService(),
                "ExecuteConfirmedExactMoveResourceAsync",
                request,
                binding,
                cancellationToken);
        }

        var initialDigest = (ReadOnlyMemory<byte>)Property(initialReview, "IntentDigest");
        initialDigest.Length.ShouldBe(SHA256.HashSizeInBytes);
        Property(initialReview, "BindingRevision").ShouldNotBeNull();
        var confirmedService = createService();
        var confirmedReview = await InvokeAsync(
            confirmedService,
            "ReviewExactMoveResourceAsync",
            request,
            cancellationToken);
        OptionalProperty(confirmedReview, "Outcome").ShouldBeNull();
        var confirmedDigest = (ReadOnlyMemory<byte>)Property(confirmedReview, "IntentDigest");
        confirmedDigest.Length.ShouldBe(SHA256.HashSizeInBytes);
        CryptographicOperations.FixedTimeEquals(initialDigest.Span, confirmedDigest.Span).ShouldBeTrue();
        return await InvokeAsync(
            confirmedService,
            "ExactMoveResourceAsync",
            request,
            cancellationToken);
    }

    private static async Task<object> ExecuteShapeScenarioAsync(
        Func<ICalendarService> createService,
        CalendarExactMoveRequest request,
        string mode,
        string unrelatedShape,
        CancellationToken cancellationToken)
    {
        if (mode == "server-authoritative" || unrelatedShape == "opaque")
            return await ExecuteTwoRoundMrtrAsync(createService, request, mode, cancellationToken);
        var review = await InvokeAsync(
            createService(),
            "ReviewExactMoveResourceAsync",
            request,
            cancellationToken);
        return Property(review, "Outcome");
    }

    private static void AssertShapeTrace(
        ObservationHandler handler,
        string mode,
        string unrelatedShape,
        int destinationCardinality)
    {
        if (mode == "server-authoritative")
        {
            handler.PropFindCount.ShouldBe(4);
            handler.ReportCount.ShouldBe(0);
            handler.SourceGetCount.ShouldBe(3);
            handler.DestinationGetCount.ShouldBe(3);
            handler.UnrelatedGetCount.ShouldBe(0);
            handler.MoveCount.ShouldBe(1);
            handler.RequestCount.ShouldBe(11);
            return;
        }
        if (unrelatedShape == "opaque")
        {
            handler.PropFindCount.ShouldBe(4);
            handler.ReportCount.ShouldBe(6);
            handler.SourceGetCount.ShouldBe(4);
            handler.DestinationGetCount.ShouldBe(4);
            handler.UnrelatedGetCount.ShouldBe(destinationCardinality * 3);
            handler.MoveCount.ShouldBe(1);
            handler.RequestCount.ShouldBe((destinationCardinality * 3) + 19);
            return;
        }
        handler.PropFindCount.ShouldBe(2);
        handler.ReportCount.ShouldBe(2);
        handler.SourceGetCount.ShouldBe(1);
        handler.DestinationGetCount.ShouldBe(1);
        handler.UnrelatedGetCount.ShouldBe(1);
        handler.MoveCount.ShouldBe(0);
        handler.RequestCount.ShouldBe(7);
    }

    private static CalDavOptions OptionsForEvidence()
    {
        var options = new CalDavOptions
        {
            BaseUrl = ObservationHandler.CalendarHomeHref,
            Username = "user",
            Password = "secret",
            CalendarHrefs = $"{ObservationHandler.SourceCalendarHref},{ObservationHandler.DestinationCalendarHref}"
        };
        options.GetType().GetProperty("InteroperabilityProfile")?.SetValue(options, "radicale-3.7.8");
        return options;
    }

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

    private static async Task<object> InvokeAsync(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(candidate => candidate.Name == methodName
                && candidate.GetParameters().Length == arguments.Length);
        if (method is null)
            throw new InvalidOperationException($"{methodName} must exist for the selected evidence mode.");
        if (method.Invoke(target, arguments) is not Task task)
            throw new InvalidOperationException($"{methodName} must return Task.");
        await task;
        return Property(task, "Result");
    }

    private static object Property(object target, string propertyName) =>
        OptionalProperty(target, propertyName).ShouldNotBeNull($"{propertyName} must be present.");

    private static object? OptionalProperty(object target, string propertyName) =>
        target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);

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
