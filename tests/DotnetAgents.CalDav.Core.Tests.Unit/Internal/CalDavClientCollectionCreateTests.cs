using System.Net;
using System.Text;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalDavClientCollectionCreateTests
{
    private const string Href = "https://cal.example/calendars/user/styled/";
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace Ical = "http://apple.com/ns/ical/";

    [Fact]
    public async Task Failed_mkcalendar_reports_the_rejected_initial_properties()
    {
        var failure = new XElement(CalDav + "mkcalendar-response",
            new XElement(Dav + "propstat",
                new XElement(Dav + "prop", new XElement(Ical + "calendar-order"), new XElement(Dav + "displayname"),
                    new XElement(CalDav + "supported-calendar-component-set")),
                new XElement(Dav + "status", "HTTP/1.1 424 Failed Dependency")),
            new XElement(Dav + "propstat",
                new XElement(Dav + "prop", new XElement(Ical + "calendar-color"), new XElement(CalDav + "calendar-timezone")),
                new XElement(Dav + "status", "HTTP/1.1 403 Forbidden")));
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(failure.ToString(), Encoding.UTF8, "application/xml")
        });

        var result = await Create(handler);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.UpstreamForbidden);
        result.RejectedProperties.ShouldBe([
            new CalendarPropertyRejection("color", 403),
            new CalendarPropertyRejection("displayName", 424),
            new CalendarPropertyRejection("entityKinds", 424),
            new CalendarPropertyRejection("order", 424),
            new CalendarPropertyRejection("timeZone", 403)
        ]);
        var body = XElement.Parse(handler.Body!);
        body.Descendants(Ical + "calendar-color").Single().Value.ShouldBe("#FF2968");
        body.Descendants(Ical + "calendar-order").Single().Value.ShouldBe("2");
        body.Descendants(CalDav + "calendar-timezone").Single().Value.ShouldContain("TZID:Europe/Berlin");
    }

    [Fact]
    public async Task Successful_mkcalendar_does_not_read_a_failure_body()
    {
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new ThrowingContent()
        });

        var result = await Create(handler);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.Dispatched);
        result.RejectedProperties.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("oversized")]
    [InlineData("empty")]
    public async Task Unusable_failure_body_keeps_the_http_status_classification(string kind)
    {
        using var handler = new Handler(() =>
        {
            HttpContent content = kind switch
            {
                "unreadable" => new ThrowingContent(),
                "oversized" => new StringContent("<x/>", Encoding.UTF8, "application/xml"),
                _ => new StringContent(string.Empty)
            };
            if (kind == "oversized")
                content.Headers.ContentLength = long.MaxValue;
            return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = content };
        });

        var result = await Create(handler);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.Conflict);
        result.RejectedProperties.ShouldBeEmpty();
    }

    private static async Task<CalendarCollectionDispatchResult> Create(Handler handler)
    {
        using var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://cal.example/") };
        var client = new CalDavClient(httpClient, Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example/", Username = "user", Password = "password"
        }), NullLogger<CalDavClient>.Instance);
        return await client.CreateCalendarCollectionAsync(new CalendarCollectionCreateDispatchRequest(
            Href, "Styled", [CalendarEntityKind.Event],
            new CalendarCollectionInitialProperties("#FF2968", 2,
                DotnetAgents.CalDav.Core.Internal.Xml.CalendarCollectionPropertyValues.SerializeTimeZone("Europe/Berlin"))),
            TestContext.Current.CancellationToken);
    }

    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Method.Method.ShouldBe("MKCALENDAR");
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = respond();
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("connection reset while reading the failure body");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
