using System.Net;
using System.Text;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public partial class CalendarReportModuleTests
{
    private const string CalendarHref = "https://cal.example/cal/";
    private static readonly DateTimeOffset From = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly XNamespace Dav = "DAV:";

    [Fact]
    public async Task FreeBusyUsesOneNativeDepthOneReportWithoutAdvertisementOrBodyFallback()
    {
        using var fixture = new Fixture();
        fixture.Add(200, BusyResponse(), "text/calendar");

        var result = await fixture.Module.FreeBusyAsync(new(CalendarHref, From, From.AddDays(1)), CancellationToken.None);

        result.Outcome.ShouldBe("success");
        result.TemporalAuthority.ShouldBe("server");
        result.Complete.ShouldBeTrue();
        result.Periods.Count.ShouldBe(1);
        fixture.Handler.Requests.Count.ShouldBe(1);
        var request = fixture.Handler.Requests[0];
        request.Method.ShouldBe("REPORT");
        request.Href.ShouldBe(CalendarHref);
        request.Depth.ShouldBe("1");
        var report = XDocument.Parse(request.Body).Root!;
        report.Name.LocalName.ShouldBe("free-busy-query");
        report.Elements().Single().Attribute("start")!.Value.ShouldBe("20260905T000000Z");
        request.Body.ShouldNotContain("calendar-data");
    }

    [Theory]
    [InlineData("FBTYPE:BUSY")]
    [InlineData("RRULE:FREQ=DAILY;COUNT=3")]
    [InlineData("RDATE:20260905T010000Z")]
    [InlineData("EXDATE:20260905T010000Z")]
    public async Task HttpSuccessWithMalformedBusyRepresentationFailsWithoutBodyFallback(string property)
    {
        using var fixture = new Fixture();
        fixture.Add(200, BusyResponse().Replace("FREEBUSY:20260905T010000Z/PT1H", property, StringComparison.Ordinal), "text/calendar");

        var exception = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.FreeBusyAsync(
            new(CalendarHref, From, From.AddDays(1)), CancellationToken.None));

        exception.Code.ShouldBe("upstream_protocol_error");
        exception.Message.ShouldContain("availability cannot be inferred");
        fixture.Handler.Requests.Count.ShouldBe(1);
        fixture.Handler.Requests[0].Method.ShouldBe("REPORT");
    }

    [Theory]
    [InlineData("FREEBUSY")]
    [InlineData("group.freebusy")]
    public async Task MisplacedNativePeriodsCannotReturnCompleteEmptyAvailability(string propertyName)
    {
        using var fixture = new Fixture();
        var body = BusyResponse().Replace("FREEBUSY:20260905T010000Z/PT1H", string.Empty, StringComparison.Ordinal)
            .Replace("BEGIN:VFREEBUSY", propertyName + ":20260905T010000Z/PT1H\r\nBEGIN:VFREEBUSY", StringComparison.Ordinal);
        fixture.Add(200, body, "text/calendar");

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.FreeBusyAsync(
            new(CalendarHref, From, From.AddDays(1)), CancellationToken.None));

        error.Code.ShouldBe("upstream_protocol_error");
        error.Message.ShouldContain("availability cannot be inferred");
        fixture.Handler.Requests.Count.ShouldBe(1);
        fixture.Handler.Requests[0].Method.ShouldBe("REPORT");
    }

    [Fact]
    public async Task FreeBusyWithoutComponentBoundsStillReturnsTheExactRequestedWindow()
    {
        using var fixture = new Fixture();
        var body = BusyResponse()
            .Replace("DTSTART:20260905T000000Z\r\n", "UID:report-1\r\n", StringComparison.Ordinal)
            .Replace("DTEND:20260906T000000Z\r\n", "DTSTAMP:20260905T000000Z\r\n", StringComparison.Ordinal);
        fixture.Add(200, body, "text/calendar");

        var result = await fixture.Module.FreeBusyAsync(new(CalendarHref, From, From.AddDays(1)), CancellationToken.None);

        result.From.ShouldBe("2026-09-05T00:00:00Z");
        result.To.ShouldBe("2026-09-06T00:00:00Z");
        result.Complete.ShouldBeTrue();
        result.Periods.Count.ShouldBe(1);
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("", "/cal/")]
    [InlineData(SyncTruncationError, "/cal/")]
    [InlineData("", "/cal")]
    [InlineData(SyncTruncationError, "/cal")]
    [InlineData("", "https://cal.example/cal")]
    [InlineData(SyncTruncationError, "https://cal.example/cal")]
    public async Task NativeInitialTruncationCompletesAndKeepsPriorCheckpointReplayable(string error, string selfHref)
    {
        using var fixture = new Fixture();
        var truncated = Self507(error).Replace("<d:href>/cal/</d:href>", $"<d:href>{selfHref}</d:href>", StringComparison.Ordinal);
        fixture.Add(207, SyncResponse("urn:sync:one", Changed("a.ics") + truncated));
        fixture.Add(207, SyncResponse("urn:sync:two", Changed("b.ics")));
        fixture.Add(207, SyncResponse("urn:sync:two", string.Empty));
        fixture.Add(207, SyncResponse("urn:sync:two", Changed("b.ics")));

        var first = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref, 1), CancellationToken.None);
        var second = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(first.Checkpoint, 1), CancellationToken.None);
        var third = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(second.Checkpoint), CancellationToken.None);
        var replay = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(first.Checkpoint, 1), CancellationToken.None);

        first.Mode.ShouldBe("initial");
        first.HasMore.ShouldBeTrue();
        second.Mode.ShouldBe("initial");
        second.HasMore.ShouldBeFalse();
        third.Mode.ShouldBe("incremental");
        third.Changes.ShouldBeEmpty();
        third.CheckpointLifetime.ShouldBe("session");
        third.RemovalMeaning.ShouldBe("removed_from_view");
        replay.Mode.ShouldBe("initial");
        replay.HasMore.ShouldBeFalse();
        replay.Changes.ShouldBe(second.Changes);
        replay.Checkpoint.ShouldBe(second.Checkpoint);
        fixture.Handler.Requests.Count.ShouldBe(4);
        var reports = fixture.Handler.Requests.Select(request => XDocument.Parse(request.Body).Root!).ToArray();
        reports[0].Element(Dav + "sync-token")!.Value.ShouldBeEmpty();
        reports[1].Element(Dav + "sync-token")!.Value.ShouldBe("urn:sync:one");
        reports[2].Element(Dav + "sync-token")!.Value.ShouldBe("urn:sync:two");
        reports[3].Element(Dav + "sync-token")!.Value.ShouldBe("urn:sync:one");
        reports[0].Element(Dav + "limit")!.Element(Dav + "nresults")!.Value.ShouldBe("1");
        reports.ShouldAllBe(report => report.Element(Dav + "sync-level")!.Value == "1");
        fixture.Handler.Requests.ShouldAllBe(request => request.Depth == "0" && request.Method == "REPORT" && request.Href == CalendarHref);
        reports.ShouldAllBe(report => report.Element(Dav + "prop")!.Elements().Single().Name == Dav + "getetag");
    }

    [Fact]
    public async Task AuthenticatedContinuationDoesNotRepeatDiscoveryWithAnUnrestrictedScope()
    {
        using var fixture = new Fixture();
        fixture.Options.CalendarHrefs = null;
        var binding = fixture.Protector.ConfigurationBinding(fixture.Options);
        var checkpoint = fixture.Protector.Protect(new(CalendarHref, "urn:sync:one", false, binding));
        fixture.Add(207, SyncResponse("urn:sync:one", string.Empty));

        await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(checkpoint), CancellationToken.None);

        fixture.Handler.Requests.Count.ShouldBe(1);
        fixture.Handler.Requests[0].Method.ShouldBe("REPORT");
    }

    [Fact]
    public async Task ExplicitInitialTombstonesAreSafeRemovalObservations()
    {
        using var fixture = new Fixture();
        fixture.Add(207, SyncResponse("urn:sync:one", "<d:response><d:href>/cal/deleted.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>"));

        var result = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None);

        result.Mode.ShouldBe("initial");
        result.Changes.Single().Kind.ShouldBe("removed");
        result.Changes.Single().Etag.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task InvalidPageSizeDoesNoRemoteWork(int pageSize)
    {
        using var fixture = new Fixture();
        (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref, pageSize), CancellationToken.None))).Code.ShouldBe("invalid_input");
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidWindowsDoNoRemoteWork()
    {
        using var fixture = new Fixture();
        foreach (var request in new[]
        {
            new CalendarFreeBusyRequest(CalendarHref, From, From),
            new CalendarFreeBusyRequest(CalendarHref, From, From.AddDays(-1)),
            new CalendarFreeBusyRequest(CalendarHref, From, From.AddDays(367)),
            new CalendarFreeBusyRequest(CalendarHref, From.AddTicks(1), From.AddDays(1)),
            new CalendarFreeBusyRequest(CalendarHref, From, From.AddDays(1).AddTicks(1)),
            new CalendarFreeBusyRequest(CalendarHref, From.ToOffset(TimeSpan.FromHours(-3)), From.AddDays(1)),
            new CalendarFreeBusyRequest(CalendarHref, From, From.AddDays(1).ToOffset(TimeSpan.FromHours(3)))
        })
            (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.FreeBusyAsync(request, CancellationToken.None)))
                .Code.ShouldBe("invalid_input");
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("https://other.example/cal/", "invalid_input")]
    [InlineData("https://cal.example/other/", "outside_scope")]
    [InlineData("https://cal.example/cal", "invalid_input")]
    public async Task HrefValidationAndScopeApplyBeforeRemoteWork(string href, string code)
    {
        using var fixture = new Fixture();
        (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(href), CancellationToken.None))).Code.ShouldBe(code);
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task TamperedCheckpointAndChangedConfigurationCannotDispatch()
    {
        using var fixture = new Fixture();
        (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Continue("untrusted"), CancellationToken.None))).Code.ShouldBe("sync_reset_required");
        fixture.Handler.Requests.ShouldBeEmpty();
        fixture.Add(207, SyncResponse("urn:sync:one", string.Empty));
        var first = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None);
        fixture.Options.Password = "changed-password";
        (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Continue(first.Checkpoint), CancellationToken.None))).Code.ShouldBe("sync_reset_required");
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EvictedCheckpointRequiresResetBeforeAnyRemoteRequest()
    {
        using var fixture = new Fixture();
        var binding = fixture.Protector.ConfigurationBinding(fixture.Options);
        var prior = fixture.Protector.Protect(new(CalendarHref, "urn:sync:prior", false, binding, OmitLimit: true));
        for (var index = 0; index < CalendarSyncCheckpointProtector.MaximumCheckpoints; index++)
            fixture.Protector.Protect(new(CalendarHref, $"urn:sync:{index}", false, binding));

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Continue(prior), CancellationToken.None));

        error.Code.ShouldBe("sync_reset_required");
        error.Message.ShouldContain("evicted");
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConfigurationChangeDuringReportNeverMintsCheckpointForAnotherContext()
    {
        using var fixture = new Fixture();
        fixture.Add(207, SyncResponse("urn:sync:one", string.Empty));
        fixture.Handler.BeforeReply = () => fixture.Options.Password = "rotated";

        (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None))).Code.ShouldBe("sync_reset_required");
    }

    [Theory]
    [InlineData(403, "<d:valid-sync-token/>", "sync_reset_required")]
    [InlineData(409, "<d:valid-sync-token/>", "sync_reset_required")]
    [InlineData(403, "<d:supported-report/>", "unsupported_capability")]
    [InlineData(403, "<d:number-of-matches-within-limits/>", "limit_exhausted")]
    [InlineData(507, "", "limit_exhausted")]
    public async Task NativeErrorConditionsAreActionableWithoutAdvancingState(int status, string error, string code)
    {
        using var fixture = new Fixture();
        fixture.Add(status, "<d:error xmlns:d='DAV:'>" + error + "</d:error>");

        var failure = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None));

        failure.Code.ShouldBe(code);
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(403, "")]
    [InlineData(403, "not xml")]
    [InlineData(403, "<d:multistatus xmlns:d='DAV:'><d:valid-sync-token/></d:multistatus>")]
    [InlineData(403, "<d:error xmlns:d='DAV:'><d:unrecognized/></d:error>")]
    [InlineData(401, "")]
    [InlineData(404, "")]
    [InlineData(405, "")]
    [InlineData(429, "")]
    [InlineData(500, "")]
    [InlineData(501, "")]
    [InlineData(302, "")]
    public async Task HttpFailureNeverFallsBackToResourceBodies(int status, string body)
    {
        using var fixture = new Fixture();
        fixture.Add(status, body);
        var failure = await Should.ThrowAsync<HttpRequestException>(() => fixture.Module.FreeBusyAsync(
            new(CalendarHref, From, From.AddDays(1)), CancellationToken.None));

        failure.StatusCode.ShouldBe((HttpStatusCode)status);
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task WrongMediaTypeAndResponseIdentityFail()
    {
        using var wrongMedia = new Fixture();
        wrongMedia.Add(200, BusyResponse(), "text/html");
        (await Should.ThrowAsync<CalendarProtocolException>(() => wrongMedia.Module.FreeBusyAsync(
            new(CalendarHref, From, From.AddDays(1)), CancellationToken.None))).Code.ShouldBe("upstream_protocol_error");
        using var redirected = new Fixture();
        redirected.Handler.Replies.Enqueue(new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cal.example/other/"),
            Content = new StringContent(SyncResponse("urn:sync:one", string.Empty))
        });
        (await Should.ThrowAsync<CalendarProtocolException>(() => redirected.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None))).Code.ShouldBe("upstream_protocol_error");
    }

    [Fact]
    public async Task TransportByteLimitRejectsOversizedResponseWithoutAResult()
    {
        using var fixture = new Fixture();
        fixture.Add(200, new string('x', 4 * 1024 * 1024 + 1), "text/calendar");
        (await Should.ThrowAsync<HttpRequestException>(() => fixture.Module.FreeBusyAsync(
            new(CalendarHref, From, From.AddDays(1)), CancellationToken.None))).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    private static string BusyResponse() => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\nBEGIN:VFREEBUSY\r\n"
        + "DTSTART:20260905T000000Z\r\nDTEND:20260906T000000Z\r\nFREEBUSY:20260905T010000Z/PT1H\r\nEND:VFREEBUSY\r\nEND:VCALENDAR\r\n";

    private static string SyncResponse(string token, string responses) => "<d:multistatus xmlns:d='DAV:'>" + responses
        + "<d:sync-token>" + token + "</d:sync-token></d:multistatus>";

    private static string Changed(string href) => "<d:response><d:href>/cal/" + href + "</d:href><d:propstat><d:prop><d:getetag>&quot;r1&quot;</d:getetag>"
        + "</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

    private const string SyncTruncationError = "<d:error><d:number-of-matches-within-limits/></d:error>";

    private static string Self507(string error = SyncTruncationError) => "<d:response><d:href>/cal/</d:href>"
        + "<d:status>HTTP/1.1 507 Insufficient Storage</d:status>" + error + "</d:response>";

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _httpClient;
        public NativeHandler Handler { get; } = new();
        public CalendarSyncCheckpointProtector Protector { get; } = new();
        public CalDavOptions Options { get; } = new() { BaseUrl = "https://cal.example", Username = "user", Password = "password", CalendarHrefs = CalendarHref };
        public CalendarReportModule Module { get; }

        public Fixture()
        {
            _httpClient = new HttpClient(Handler);
            var options = Microsoft.Extensions.Options.Options.Create(Options);
            var client = new CalDavClient(_httpClient, options, NullLogger<CalDavClient>.Instance);
            Module = new CalendarReportModule(client, options, Protector);
        }

        public void Add(int status, string body, string contentType = "application/xml") => Handler.Replies.Enqueue(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        });

        public void Dispose() => _httpClient.Dispose();
    }

    private sealed class NativeHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Replies { get; } = new();
        public List<ObservedRequest> Requests { get; } = [];
        public Action? BeforeReply { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.GetValues("Depth").Single(), body));
            BeforeReply?.Invoke();
            return Replies.Dequeue();
        }
    }

    private sealed record ObservedRequest(string Method, string Href, string Depth, string Body);
}
