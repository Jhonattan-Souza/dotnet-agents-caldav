using System.Net;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Internal;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public partial class CalDavClientTests
{
    [Fact]
    public async Task CollectionDelete_AutoScheduleServerDeletesAnEmptyCalendarAfterAStableScan()
    {
        var server = new AutoScheduleCalendarServer();

        var result = await DeleteScannedCollectionAsync(server);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.Dispatched);
        server.Methods.ShouldBe(["OPTIONS", "PROPFIND", "PROPFIND", "DELETE"]);
        server.Depths.ShouldAllBe(depth => depth == "1");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(120, 3)]
    public async Task CollectionDelete_AutoScheduleServerDeletesMembersWithoutParticipation(int count, int batches)
    {
        var server = new AutoScheduleCalendarServer();
        server.Add("text.ics", "SUMMARY:ATTENDEE:mailto:x\r\nDESCRIPTION:ORGANIZER notes\r\n");
        for (var index = 1; index < count; index++)
            server.Add($"event-{index}.ics", "SUMMARY:Ordinary\r\n");

        var result = await DeleteScannedCollectionAsync(server);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.Dispatched);
        server.Methods.ShouldBe(
            ["OPTIONS", "PROPFIND", .. Enumerable.Repeat("REPORT", batches), "PROPFIND", "DELETE"]);
        server.RequestedMembers.ShouldBe(count);
    }

    [Theory]
    [InlineData("ATTENDEE:mailto:guest@example.com\r\n")]
    [InlineData("ORGANIZER;CN=Owner:mailto:owner@example.com\r\n")]
    [InlineData("group.attendee:mailto:guest@example.com\r\n")]
    [InlineData("BEGIN:VALARM\r\nACTION:EMAIL\r\nTRIGGER:-PT5M\r\nSUMMARY:Soon\r\nDESCRIPTION:Soon\r\n"
        + "ATTENDEE:mailto:self@example.com\r\nEND:VALARM\r\n")]
    public async Task CollectionDelete_AutoScheduleServerKeepsTheProofRequirementForParticipation(string lines)
    {
        var server = new AutoScheduleCalendarServer();
        server.Add("ordinary.ics", "SUMMARY:Ordinary\r\n");
        server.Add("participation.ics", lines);

        var result = await DeleteScannedCollectionAsync(server);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.SchedulingUnsafe);
        server.Methods.ShouldBe(["OPTIONS", "PROPFIND", "REPORT"]);
    }

    [Fact]
    public async Task CollectionDelete_MemberCountOverTheQueryBudgetFailsClosedBeforeFetchingData()
    {
        var server = new AutoScheduleCalendarServer();
        for (var index = 0; index <= CalDavClient.MaximumScannedCollectionMembers; index++)
            server.Add($"event-{index}.ics", "SUMMARY:Ordinary\r\n");

        var result = await DeleteScannedCollectionAsync(server);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.SchedulingUnsafe);
        server.Methods.ShouldBe(["OPTIONS", "PROPFIND"]);
    }

    [Fact]
    public async Task CollectionDelete_MemberCountAtTheQueryBudgetIsScanned()
    {
        var server = new AutoScheduleCalendarServer();
        for (var index = 0; index < CalDavClient.MaximumScannedCollectionMembers; index++)
            server.Add($"event-{index}.ics", "SUMMARY:Ordinary\r\n");

        var result = await DeleteScannedCollectionAsync(server);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.Dispatched);
        server.RequestedMembers.ShouldBe(CalDavClient.MaximumScannedCollectionMembers);
    }

    [Fact]
    public async Task CollectionDelete_ScannedBytesOverTheQueryBudgetFailClosed()
    {
        var server = new AutoScheduleCalendarServer();
        var description = "DESCRIPTION:" + new string('x', (int)(3.9 * 1024 * 1024)) + "\r\n";
        var members = (int)(CalDavClient.MaximumScannedCollectionBytes / (3.9 * 1024 * 1024)) + 1;
        for (var index = 0; index < members; index++)
            server.Add($"large-{index}.ics", description);

        var result = await DeleteScannedCollectionAsync(server);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.SchedulingUnsafe);
        server.Methods.ShouldBe(["OPTIONS", "PROPFIND", "REPORT"]);
    }

    [Theory]
    [InlineData("propfind_http")]
    [InlineData("propfind_io")]
    [InlineData("propfind_timeout")]
    [InlineData("propfind_status")]
    [InlineData("propfind_malformed")]
    [InlineData("propfind_redirect")]
    [InlineData("propfind_too_large")]
    [InlineData("report_http")]
    [InlineData("report_status")]
    [InlineData("report_unsupported")]
    [InlineData("report_malformed")]
    [InlineData("report_unsafe_href")]
    [InlineData("report_cancellation")]
    [InlineData("second_propfind_io")]
    [InlineData("second_propfind_redirect")]
    public async Task CollectionDelete_ScanTransportFailureFailsClosed(string failure)
    {
        var server = new AutoScheduleCalendarServer { Failure = failure };
        server.Add("ordinary.ics", "SUMMARY:Ordinary\r\n");

        var result = await DeleteScannedCollectionAsync(server);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.SchedulingUnsafe);
        server.Methods.ShouldNotContain("DELETE");
    }

    [Theory]
    [InlineData("options_caller_cancellation", new[] { "OPTIONS" })]
    [InlineData("propfind_caller_cancellation", new[] { "OPTIONS", "PROPFIND" })]
    public async Task CollectionDelete_CancellationBeforeDispatchIsReportedAsNotDispatched(
        string failure,
        string[] methods)
    {
        using var cancellation = new CancellationTokenSource();
        var server = new AutoScheduleCalendarServer { Failure = failure, Cancellation = cancellation };
        server.Add("ordinary.ics", "SUMMARY:Ordinary\r\n");

        var result = await CreateSut(new StubHttpMessageHandler(server.Handle))
            .DeleteCalendarCollectionAsync(AutoScheduleCalendarServer.CalendarHref, cancellation.Token);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.CanceledBeforeDispatch);
        server.Methods.ShouldBe(methods);
    }

    [Theory]
    [InlineData("added")]
    [InlineData("changed")]
    [InlineData("removed")]
    [InlineData("replaced")]
    public async Task CollectionDelete_MembershipDriftAfterTheScanFailsClosed(string drift)
    {
        var server = new AutoScheduleCalendarServer();
        server.Add("ordinary.ics", "SUMMARY:Ordinary\r\n");
        server.Add("second.ics", "SUMMARY:Second\r\n");
        server.AfterReport = () =>
        {
            if (drift == "added")
                server.Add("invitation.ics", "ATTENDEE:mailto:guest@example.com\r\n");
            else if (drift == "changed")
                server.Add("second.ics", "SUMMARY:Second\r\nATTENDEE:mailto:guest@example.com\r\n");
            else if (drift == "removed")
                server.Remove("second.ics");
            else
            {
                server.Remove("second.ics");
                server.Add("third.ics", "SUMMARY:Third\r\n");
            }
        };

        var result = await DeleteScannedCollectionAsync(server);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.SchedulingUnsafe);
        server.Methods.ShouldBe(["OPTIONS", "PROPFIND", "REPORT", "PROPFIND"]);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("removed")]
    public async Task CollectionDelete_MemberRevisionDriftDuringTheScanFailsClosed(string drift)
    {
        var server = new AutoScheduleCalendarServer();
        server.Add("ordinary.ics", "SUMMARY:Ordinary\r\n");
        server.AfterListing = () =>
        {
            if (drift == "changed")
                server.Add("ordinary.ics", "SUMMARY:Changed\r\n");
            else
                server.Remove("ordinary.ics");
        };

        var result = await DeleteScannedCollectionAsync(server);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.SchedulingUnsafe);
        server.Methods.ShouldBe(["OPTIONS", "PROPFIND", "REPORT"]);
    }

    [Theory]
    [InlineData("<d:resourcetype><d:collection/></d:resourcetype><d:getetag>\"c1\"</d:getetag>", "200 OK", "nested/")]
    [InlineData("<d:resourcetype/><d:getetag>W/\"weak\"</d:getetag>", "200 OK", "weak.ics")]
    [InlineData("<d:resourcetype/>", "200 OK", "untagged.ics")]
    [InlineData("<d:resourcetype/><d:getetag>\"a1\"</d:getetag>", "404 Not Found", "unknown.ics")]
    [InlineData("<d:resourcetype/><d:getetag>\"x1\"</d:getetag>", "200 OK", "/calendars/user/other/escaped.ics")]
    [InlineData("<d:resourcetype/><d:getetag>\"x1\"</d:getetag>", "200 OK", "https://elsewhere.example/calendars/user/work/x.ics")]
    public async Task CollectionDelete_UnscannableMemberFailsClosed(string properties, string status, string href)
    {
        var server = new AutoScheduleCalendarServer
        {
            ExtraListingResponse = $"<d:response><d:href>{href}</d:href><d:propstat><d:prop>{properties}</d:prop>"
                + $"<d:status>HTTP/1.1 {status}</d:status></d:propstat></d:response>"
        };
        server.Add("ordinary.ics", "SUMMARY:Ordinary\r\n");

        var result = await DeleteScannedCollectionAsync(server);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.SchedulingUnsafe);
        server.Methods.ShouldBe(["OPTIONS", "PROPFIND"]);
    }

    [Fact]
    public async Task CollectionDelete_DuplicateMemberListingFailsClosed()
    {
        var server = new AutoScheduleCalendarServer
        {
            ExtraListingResponse = "<d:response><d:href>/calendars/user/work/ordinary.ics</d:href><d:propstat><d:prop>"
                + "<d:resourcetype/><d:getetag>\"other\"</d:getetag></d:prop>"
                + "<d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
        };
        server.Add("ordinary.ics", "SUMMARY:Ordinary\r\n");

        var result = await DeleteScannedCollectionAsync(server);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.SchedulingUnsafe);
        server.Methods.ShouldBe(["OPTIONS", "PROPFIND"]);
    }

    private Task<CalendarCollectionDispatchResult> DeleteScannedCollectionAsync(AutoScheduleCalendarServer server) =>
        CreateSut(new StubHttpMessageHandler(server.Handle))
            .DeleteCalendarCollectionAsync(AutoScheduleCalendarServer.CalendarHref, TestContext.Current.CancellationToken);

    /// <summary>An in-memory Calendar on a server that advertises automatic scheduling.</summary>
    private sealed class AutoScheduleCalendarServer
    {
        internal const string CalendarHref = "https://example.com/calendars/user/work/";
        private const string CalendarPath = "/calendars/user/work/";
        private static readonly XNamespace Dav = "DAV:";
        private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
        private readonly SortedDictionary<string, (string EntityTag, string Data)> _members = new(StringComparer.Ordinal);
        private int _revision;
        private int _listings;

        internal List<string> Methods { get; } = [];
        internal List<string?> Depths { get; } = [];
        internal int RequestedMembers { get; private set; }
        internal string? Failure { get; init; }
        internal CancellationTokenSource? Cancellation { get; init; }
        internal string ExtraListingResponse { get; init; } = string.Empty;
        internal Action? AfterListing { get; set; }
        internal Action? AfterReport { get; set; }

        internal void Add(string name, string lines) => _members[name] = (
            $"\"r{++_revision}\"",
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:{name}\r\n"
            + $"DTSTAMP:20260901T000000Z\r\nDTSTART:20260902T100000Z\r\n{lines}END:VEVENT\r\nEND:VCALENDAR\r\n");

        internal void Remove(string name) => _members.Remove(name);

        internal HttpResponseMessage Handle(HttpRequestMessage request)
        {
            Methods.Add(request.Method.Method);
            return request.Method.Method switch
            {
                "OPTIONS" => Options(),
                "PROPFIND" => Listing(request),
                "REPORT" => Multiget(request),
                "DELETE" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException("Unexpected HTTP work")
            };
        }

        private HttpResponseMessage Options()
        {
            if (Failure == "options_caller_cancellation")
                throw CancelCaller();
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("DAV", "1, 3, calendar-access, calendar-auto-schedule");
            return response;
        }

        private HttpResponseMessage Listing(HttpRequestMessage request)
        {
            Depths.Add(request.Headers.TryGetValues("Depth", out var depth) ? depth.Single() : null);
            var listing = ++_listings;
            var failure = listing == 1 ? FailListing(Failure) : Failure == "second_propfind_io" ? new IOException("reset") : null;
            if (failure is not null)
                throw failure;
            if (listing == 1 && Failure == "propfind_status")
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (listing == 1 && Failure == "propfind_malformed")
                return MultiStatus("<d:multistatus xmlns:d='DAV:'><d:response>");
            if (listing == 1 && Failure == "propfind_redirect" || listing == 2 && Failure == "second_propfind_redirect")
                return Redirect();
            if (listing == 1 && Failure == "propfind_too_large")
                return MultiStatus(new string(' ', 4 * 1024 * 1024 + 1));
            var body = "<d:multistatus xmlns:d='DAV:'>"
                + $"<d:response><d:href>{CalendarPath}</d:href><d:propstat><d:prop>"
                + "<d:resourcetype><d:collection/><c:calendar xmlns:c='urn:ietf:params:xml:ns:caldav'/></d:resourcetype>"
                + "<d:getetag>\"collection\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                + string.Concat(_members.Select(member => $"<d:response><d:href>{CalendarPath}{member.Key}</d:href>"
                    + $"<d:propstat><d:prop><d:resourcetype/><d:getetag>{member.Value.EntityTag}</d:getetag></d:prop>"
                    + "<d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"))
                + ExtraListingResponse
                + "</d:multistatus>";
            AfterListing?.Invoke();
            AfterListing = null;
            return MultiStatus(body);
        }

        private Exception? FailListing(string? failure) => failure switch
        {
            "propfind_http" => new HttpRequestException("reset"),
            "propfind_io" => new IOException("reset"),
            "propfind_timeout" => new TimeoutException(),
            "propfind_caller_cancellation" => CancelCaller(),
            _ => null
        };

        private OperationCanceledException CancelCaller()
        {
            Cancellation!.Cancel();
            return new OperationCanceledException(Cancellation.Token);
        }

        private static HttpResponseMessage Redirect()
        {
            var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            response.Headers.Location = new Uri("https://example.com/calendars/user/moved/");
            return response;
        }

        private HttpResponseMessage Multiget(HttpRequestMessage request)
        {
            var failure = Failure switch
            {
                "report_http" => new HttpRequestException("reset"),
                "report_cancellation" => new OperationCanceledException(),
                _ => (Exception?)null
            };
            if (failure is not null)
                throw failure;
            if (Failure == "report_status")
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            if (Failure == "report_unsupported")
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            if (Failure == "report_malformed")
                return MultiStatus("<d:multistatus xmlns:d='DAV:'><d:response>");
            if (Failure == "report_unsafe_href")
                return MultiStatus("<d:multistatus xmlns:d='DAV:'><d:response><d:href>/calendars/user/other/x.ics</d:href>"
                    + "<d:status>HTTP/1.1 404 Not Found</d:status></d:response></d:multistatus>");

            var requested = XDocument.Parse(request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken)
                    .GetAwaiter().GetResult())
                .Descendants(Dav + "href")
                .Select(href => new Uri(href.Value).AbsolutePath)
                .ToArray();
            RequestedMembers += requested.Length;
            var document = new XDocument(new XElement(Dav + "multistatus",
                new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "c", CalDav.NamespaceName),
                requested.Select(MultigetResponse)));
            AfterReport?.Invoke();
            AfterReport = null;
            return MultiStatus(document.ToString(SaveOptions.DisableFormatting));
        }

        private XElement MultigetResponse(string path) =>
            _members.TryGetValue(path[CalendarPath.Length..], out var member)
                ? new XElement(Dav + "response",
                    new XElement(Dav + "href", path),
                    new XElement(Dav + "propstat",
                        new XElement(Dav + "prop",
                            new XElement(Dav + "getetag", member.EntityTag),
                            new XElement(CalDav + "calendar-data", member.Data)),
                        new XElement(Dav + "status", "HTTP/1.1 200 OK")))
                : new XElement(Dav + "response",
                    new XElement(Dav + "href", path),
                    new XElement(Dav + "status", "HTTP/1.1 404 Not Found"));

        private static HttpResponseMessage MultiStatus(string body) => new(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/xml")
        };
    }
}
