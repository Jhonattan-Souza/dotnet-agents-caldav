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
public sealed class ExactMoveMrtrRadicaleSizeEvidenceTests(
    RadicaleConformanceFixture fixture,
    ITestOutputHelper output)
{
    private static readonly int[] CorpusSizes = [1, 50, 600];

    [Fact]
    public async Task PinnedRadicaleObservesExactMoveMrtrWorkAtOneFiftyAndSixHundredResources()
    {
        using var probe = CreateProbeClient();
        var sourceCalendar = new Uri(fixture.BaseUrl + "/conformance/exact-move-mrtr-source/", UriKind.Absolute);
        var destinationCalendar = new Uri(
            fixture.BaseUrl + "/conformance/exact-move-mrtr-destination/",
            UriKind.Absolute);
        var sourceCreated = false;
        var destinationCreated = false;
        try
        {
            (await CreateCalendarAsync(probe, sourceCalendar, "Exact Move MRTR Source"))
                .ShouldBe(HttpStatusCode.Created);
            sourceCreated = true;
            (await CreateCalendarAsync(probe, destinationCalendar, "Exact Move MRTR Destination"))
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
                var uid = $"pinned-exact-move-mrtr-{destinationCardinality}";
                var sourceContent = Todo(uid);
                var source = await PutResourceAsync(
                    probe,
                    new Uri(sourceCalendar, $"reviewed-{destinationCardinality}.ics"),
                    sourceContent);
                var destinationHref = new Uri(destinationCalendar, $"moved-{destinationCardinality}.ics");
                var request = new CalendarExactMoveRequest(
                    new CalendarResourceRevisionReference(
                        source.Href.AbsoluteUri,
                        uid,
                        CalendarEntityKind.Todo,
                        source.EntityTag),
                    destinationHref.AbsoluteUri);
                var wire = new ExactMoveTraceFilter(source.Href, destinationCalendar, destinationHref);
                var started = Stopwatch.GetTimestamp();

                var result = await ExecuteTwoRoundMrtrAsync(
                    fixture.BaseUrl,
                    $"{sourceCalendar.AbsoluteUri},{destinationCalendar.AbsoluteUri}",
                    wire,
                    request,
                    TestContext.Current.CancellationToken);

                var elapsed = Stopwatch.GetElapsedTime(started);
                result.Code.ShouldBe(CalendarExactResourceCode.Success);
                result.MutationState.ShouldBe(CalendarMutationState.Committed);
                var snapshot = result.Snapshot.ShouldNotBeNull();
                var observedContent = snapshot.AuthoritativeUtf8;
                observedContent.Span.SequenceEqual(source.AuthoritativeUtf8.Span).ShouldBeTrue();
                AssertConstantWorkTrace(wire);
                foreach (var index in new[] { 0, destinationCardinality / 2, destinationCardinality - 1 }.Distinct())
                {
                    var observed = await SendAsync(
                        probe,
                        HttpMethod.Get,
                        new Uri(destinationCalendar, $"unrelated-{index}.ics"));
                    observed.Status.ShouldBe(HttpStatusCode.OK);
                    observed.EntityTag.ShouldNotBeNull();
                }
                var movedHref = new Uri(snapshot.ResourceHref, UriKind.Absolute);
                var movedEntityTag = snapshot.EntityTag;
                (await SendAsync(
                    probe,
                    HttpMethod.Delete,
                    movedHref,
                    content: null,
                    ("If-Match", movedEntityTag))).Status.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    Evidence = "CAL-EVIDENCE-013",
                    Operation = "exact-move-mrtr",
                    Implementation = "server-authoritative",
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
                await ObserveOpaqueOversizedCorpusAsync(
                    probe,
                    sourceCalendar,
                    destinationCalendar,
                    destinationCardinality);
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

    private async Task ObserveOpaqueOversizedCorpusAsync(
        HttpClient probe,
        Uri sourceCalendar,
        Uri destinationCalendar,
        int destinationCardinality)
    {
        var ordinaryHref = new Uri(destinationCalendar, "unrelated-0.ics");
        var ordinary = await SendAsync(probe, HttpMethod.Get, ordinaryHref);
        ordinary.Status.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(
            probe,
            HttpMethod.Delete,
            ordinaryHref,
            content: null,
            ("If-Match", ordinary.EntityTag.ShouldNotBeNull()))).Status
            .ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        var hazardHref = new Uri(destinationCalendar, "000-opaque-oversized.ics");
        var hazard = await PutResourceAsync(probe, hazardHref, OpaqueOversizedEvent());
        hazard.AuthoritativeUtf8.Length.ShouldBeGreaterThan(4 * 1024 * 1024);
        EntityTagHeaderValue.Parse(hazard.EntityTag).IsWeak.ShouldBeFalse();
        Encoding.UTF8.GetString(hazard.AuthoritativeUtf8.Span[..256]).ShouldContain("CALSCALE:X-CUSTOM");
        if (destinationCardinality == 1)
            await AssertServerAcceptedOpaqueGrammarAsync(probe, sourceCalendar, destinationCalendar);
        (await CountKindAsync(probe, destinationCalendar, "VTODO")).ShouldBe(destinationCardinality - 1);
        (await CountKindAsync(probe, destinationCalendar, "VEVENT")).ShouldBe(1);
        var uid = $"pinned-exact-move-mixed-{destinationCardinality}";
        var source = await PutResourceAsync(
            probe,
            new Uri(sourceCalendar, $"mixed-{destinationCardinality}.ics"),
            Todo(uid));
        var destinationHref = new Uri(destinationCalendar, $"mixed-moved-{destinationCardinality}.ics");
        var request = new CalendarExactMoveRequest(
            new CalendarResourceRevisionReference(
                source.Href.AbsoluteUri,
                uid,
                CalendarEntityKind.Todo,
                source.EntityTag),
            destinationHref.AbsoluteUri);
        var wire = new ExactMoveTraceFilter(source.Href, destinationCalendar, destinationHref);
        var started = Stopwatch.GetTimestamp();
        string? movedEntityTag = null;
        try
        {
            var result = await ExecuteTwoRoundMrtrAsync(
                fixture.BaseUrl,
                $"{sourceCalendar.AbsoluteUri},{destinationCalendar.AbsoluteUri}",
                wire,
                request,
                TestContext.Current.CancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started);
            result.Code.ShouldBe(CalendarExactResourceCode.Success);
            result.MutationState.ShouldBe(CalendarMutationState.Committed);
            movedEntityTag = result.Snapshot.ShouldNotBeNull().EntityTag;
            AssertConstantWorkTrace(wire);
            WriteOpaqueOversizedObservation(destinationCardinality, elapsed, wire);
        }
        finally
        {
            if (movedEntityTag is not null)
            {
                (await SendAsync(
                    probe,
                    HttpMethod.Delete,
                    destinationHref,
                    content: null,
                    ("If-Match", movedEntityTag))).Status
                    .ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
            }
            else
            {
                (await SendAsync(
                    probe,
                    HttpMethod.Delete,
                    source.Href,
                    content: null,
                    ("If-Match", source.EntityTag))).Status
                    .ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
            }
            (await SendAsync(
                probe,
                HttpMethod.Delete,
                hazardHref,
                content: null,
                ("If-Match", hazard.EntityTag))).Status
                .ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
            (await SendAsync(
                probe,
                HttpMethod.Put,
                ordinaryHref,
                UnrelatedTodo(0),
                ("If-None-Match", "*"))).Status.ShouldBe(HttpStatusCode.Created);
        }
    }

    private static async Task AssertServerAcceptedOpaqueGrammarAsync(
        HttpClient probe,
        Uri sourceCalendar,
        Uri destinationCalendar)
    {
        var proofHref = new Uri(sourceCalendar, "opaque-grammar-proof.ics");
        var proof = await PutResourceAsync(probe, proofHref, OpaqueEvent(paddingLength: 0));
        try
        {
            var proofWire = new ExactMoveTraceFilter(proofHref, destinationCalendar, proofHref);
            await using var provider = CreateProvider(
                sourceCalendar.GetLeftPart(UriPartial.Authority),
                $"{sourceCalendar.AbsoluteUri},{destinationCalendar.AbsoluteUri}",
                proofWire);
            var observed = await provider.GetRequiredService<ICalendarService>()
                .GetResourceAsync(proofHref.AbsoluteUri, TestContext.Current.CancellationToken);
            observed.Code.ShouldBe(CalendarResourceReadCode.Success);
            observed.Snapshot.ShouldNotBeNull().Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        }
        finally
        {
            (await SendAsync(
                probe,
                HttpMethod.Delete,
                proofHref,
                content: null,
                ("If-Match", proof.EntityTag))).Status
                .ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        }
    }

    private static void AssertConstantWorkTrace(ExactMoveTraceFilter wire)
    {
        wire.PropFindCount.ShouldBe(10);
        wire.ReportCount.ShouldBe(0);
        wire.MultigetCount.ShouldBe(0);
        wire.SourceGetCount.ShouldBe(3);
        wire.DestinationGetCount.ShouldBe(3);
        wire.UnrelatedGetCount.ShouldBe(0);
        wire.MoveCount.ShouldBe(1);
        wire.RequestCount.ShouldBe(17);
    }

    private void WriteOpaqueOversizedObservation(
        int destinationCardinality,
        TimeSpan elapsed,
        ExactMoveTraceFilter wire) => output.WriteLine(JsonSerializer.Serialize(new
        {
            Evidence = "CAL-EVIDENCE-013",
            Operation = "exact-move-mrtr",
            Corpus = "opaque-oversized",
            Implementation = "server-authoritative",
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

    private static async Task<CalendarExactResourceResult> ExecuteTwoRoundMrtrAsync(
        string baseUrl,
        string calendarHrefs,
        ExactMoveTraceFilter wire,
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken)
    {
        await using var initialProvider = CreateProvider(baseUrl, calendarHrefs, wire);
        var initialReview = await initialProvider.GetRequiredService<ICalendarService>()
            .ReviewExactMoveResourceAsync(request, cancellationToken);
        initialReview.Outcome.ShouldBeNull();
        var binding = initialReview.Binding.ShouldNotBeNull();
        binding.SourceIntentDigest.Length.ShouldBe(SHA256.HashSizeInBytes);

        await using var confirmedProvider = CreateProvider(baseUrl, calendarHrefs, wire);
        return await confirmedProvider.GetRequiredService<ICalendarService>()
            .ExecuteConfirmedExactMoveResourceAsync(request, binding, cancellationToken);
    }

    private static ServiceProvider CreateProvider(
        string baseUrl,
        string calendarHrefs,
        ExactMoveTraceFilter wire)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = baseUrl;
            options.CalendarHrefs = calendarHrefs;
            options.Username = RadicaleConformanceFixture.Username;
            options.Password = RadicaleConformanceFixture.Password;
            options.InteroperabilityProfile = "radicale-3.7.8";
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
            + "<C:supported-calendar-component-set><C:comp name=\"VEVENT\"/><C:comp name=\"VTODO\"/>"
            + "</C:supported-calendar-component-set></D:prop></D:set></D:mkcol>";
        return (await SendAsync(client, new HttpMethod("MKCOL"), calendar, body)).Status;
    }

    private static Task<int> CountTodosAsync(HttpClient client, Uri calendar) =>
        CountKindAsync(client, calendar, "VTODO");

    private static async Task<int> CountKindAsync(HttpClient client, Uri calendar, string component)
    {
        var body = "<?xml version=\"1.0\" encoding=\"utf-8\" ?>"
            + "<C:calendar-query xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\">"
            + "<D:prop><D:getetag/></D:prop><C:filter><C:comp-filter name=\"VCALENDAR\">"
            + $"<C:comp-filter name=\"{component}\"/></C:comp-filter></C:filter></C:calendar-query>";
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
        return new SeededResource(href, observed.EntityTag.ShouldNotBeNull(), observed.Content);
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
        var responseContent = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var body = response.StatusCode == HttpStatusCode.MultiStatus
            ? Encoding.UTF8.GetString(responseContent)
            : string.Empty;
        return new ProbeResponse(response.StatusCode, response.Headers.ETag?.ToString(), body, responseContent);
    }

    private static string Todo(string uid) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Move Evidence//EN\r\nBEGIN:VTODO\r\n"
        + $"UID:{uid}\r\nDTSTAMP:20260823T120000Z\r\nSUMMARY:exact move evidence\r\n"
        + "X-EVIDENCE-LEXICAL:preserve-this-exactly\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private static string UnrelatedTodo(int index) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Move Evidence//EN\r\nBEGIN:VTODO\r\n"
        + $"UID:unrelated-{index}\r\nDTSTAMP:20260823T120000Z\r\nSUMMARY:unrelated {index}\r\n"
        + $"X-PRIVATE-{index}:opaque-value-{index}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private static string OpaqueOversizedEvent() => OpaqueEvent((4 * 1024 * 1024) + 4096);

    private static string OpaqueEvent(int paddingLength) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Move Evidence//EN\r\nCALSCALE:X-CUSTOM\r\n"
        + "BEGIN:VEVENT\r\nUID:unrelated-opaque-oversized\r\nDTSTAMP:20260823T120000Z\r\n"
        + "DTSTART:20260824T120000Z\r\nX-EVIDENCE-PADDING:"
        + new string('x', paddingLength)
        + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private sealed record SeededResource(Uri Href, string EntityTag, ReadOnlyMemory<byte> AuthoritativeUtf8);

    private sealed record ProbeResponse(HttpStatusCode Status, string? EntityTag, string Body, byte[] Content);

    private sealed class ExactMoveTraceFilter(
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

        private sealed class TraceHandler(ExactMoveTraceFilter owner) : DelegatingHandler
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
