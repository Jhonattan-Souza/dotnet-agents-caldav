using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public class CalDavClientTests
{
    [Fact]
    public async Task GetCalendarResourcesForQueryAsync_ReturnsFiveAuthoritativeResourcesFromOneMultiget()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var hrefs = Enumerable.Range(1, 5).Select(index => $"{calendarHref}{index}.ics").ToArray();
        var requests = new List<(HttpMethod Method, string Body)>();
        var responses = string.Concat(hrefs.Select((href, index) =>
            $"<d:response><d:href>{new Uri(href).AbsolutePath}</d:href><d:propstat><d:prop>"
            + $"<d:getetag>&quot;r{index + 1}&quot;</d:getetag>"
            + $"<c:calendar-data>BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:event-{index + 1}\n"
            + "END:VEVENT\nEND:VCALENDAR\n</c:calendar-data>"
            + "</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"));
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add((request.Method, request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult()));
            if (request.Method.Method == "REPORT")
            {
                return new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        $"<d:multistatus xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'>{responses}</d:multistatus>")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"fallback\"") },
                Content = new StringContent("unexpected fallback")
            };
        });
        var sut = CreateSut(handler);

        var results = await sut.GetCalendarResourcesForQueryAsync(calendarHref, hrefs, CancellationToken.None);

        results.Count.ShouldBe(5);
        results.ShouldAllBe(result => result.Code == CalendarResourceReadCode.Success);
        results.Select(result => result.ResourceHref).ShouldBe(hrefs);
        results.Select(result => result.EntityTag).ShouldBe(Enumerable.Range(1, 5).Select(index => $"\"r{index}\"").ToArray());
        Encoding.UTF8.GetString(results[0].AuthoritativeUtf8.Span).ShouldBe(
            "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:event-1\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        requests.Count.ShouldBe(1);
        requests[0].Method.Method.ShouldBe("REPORT");
        requests[0].Body.ShouldContain("calendar-multiget");
        hrefs.ShouldAllBe(href => requests[0].Body.Contains(href, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCalendarResourcesForQueryAsync_RejectsMissingResponseAtomicallyWithoutFallbackGets()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var hrefs = Enumerable.Range(1, 5).Select(index => $"{calendarHref}{index}.ics").ToArray();
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(request =>
        {
            requestCount++;
            request.Method.Method.ShouldBe("REPORT");
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent("""
                    <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                      <d:response><d:href>/calendars/user/events/2.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>
                      <d:response><d:href>/calendars/user/events/3.ics</d:href><d:propstat><d:prop><d:getetag>W/&quot;r3&quot;</d:getetag><c:calendar-data>weak</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                      <d:response><d:href>/calendars/user/events/4.ics</d:href><d:status>HTTP/1.1 500 Server Error</d:status></d:response>
                      <d:response><d:href>/calendars/user/events/5.ics</d:href><d:propstat><d:prop><d:getetag>&quot;r5&quot;</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    </d:multistatus>
                    """)
            };
        }));

        await Should.ThrowAsync<System.Xml.XmlException>(() =>
            sut.GetCalendarResourcesForQueryAsync(calendarHref, hrefs, CancellationToken.None));
        requestCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetCalendarResourceForQueryAsync_UsesCalendarMultigetAsAuthoritativeRead()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        const string resourceHref = "https://example.com/calendars/user/events/a.ics";
        var requests = new List<(HttpMethod Method, string Body)>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add((request.Method, request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult()));
            if (request.Method.Method == "REPORT")
            {
                return new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent("<d:multistatus xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'><d:response><d:href>/calendars/user/events/a.ics</d:href><d:propstat><d:prop><d:getetag>\"r1\"</d:getetag><c:calendar-data>normalized-only-for-capability</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"r1\"") },
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("authoritative\r\nX-KEEP:opaque\r\n"))
            };
        });
        var sut = CreateSut(handler);

        var result = await sut.GetCalendarResourceForQueryAsync(calendarHref, resourceHref, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        Encoding.UTF8.GetString(result.AuthoritativeUtf8.Span).ShouldBe("normalized-only-for-capability");
        requests.Select(request => request.Method.Method).ShouldBe(["REPORT"]);
        requests[0].Body.ShouldContain("calendar-multiget");
        requests[0].Body.ShouldContain("calendar-data");
        requests[0].Body.ShouldContain(resourceHref);
    }

    [Fact]
    public async Task CalendarMultigetReturnsEveryClosedResourceOutcomeFromAnExactBatch()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var hrefs = Enumerable.Range(1, 6).Select(index => $"{calendarHref}{index}.ics").ToArray();
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent("""
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response><d:href>/calendars/user/events/1.ics</d:href><d:propstat><d:prop><d:getetag>&quot;r1&quot;</d:getetag><c:calendar-data>one</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                  <d:response><d:href>/calendars/user/events/2.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>
                  <d:response><d:href>/calendars/user/events/3.ics</d:href><d:propstat><d:prop><d:getetag>W/&quot;r3&quot;</d:getetag><c:calendar-data>weak</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                  <d:response><d:href>/calendars/user/events/4.ics</d:href><d:status>HTTP/1.1 500 Server Error</d:status></d:response>
                  <d:response><d:href>/calendars/user/events/5.ics</d:href><d:propstat><d:prop><d:getetag>&quot;r5&quot;</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                  <d:response><d:href>/calendars/user/events/6.ics</d:href><d:propstat><d:prop><c:calendar-data>missing tag</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                </d:multistatus>
                """)
        }));

        var reads = await sut.GetCalendarResourcesForQueryAsync(
            calendarHref,
            hrefs,
            TestContext.Current.CancellationToken);

        reads.Select(read => read.Code).ShouldBe([
            CalendarResourceReadCode.Success,
            CalendarResourceReadCode.NotFound,
            CalendarResourceReadCode.ConcurrencyUnavailable,
            CalendarResourceReadCode.UpstreamProtocolError,
            CalendarResourceReadCode.UpstreamProtocolError,
            CalendarResourceReadCode.ConcurrencyUnavailable
        ]);
    }

    [Fact]
    public async Task CalendarMultigetEmptyBatchReturnsWithoutWireWork()
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            throw new InvalidOperationException("No request is expected for an empty batch.");
        }));

        var reads = await sut.GetCalendarResourcesForQueryAsync(
            "https://example.com/calendars/user/events/",
            [],
            TestContext.Current.CancellationToken);

        reads.ShouldBeEmpty();
        requestCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("https://example.com/calendars/user/events/", "too_many")]
    [InlineData("https://foreign.example/calendars/user/events/", "valid")]
    [InlineData("https://example.com/calendars/user/events/", "relative")]
    [InlineData("https://example.com/calendars/user/events/", "nested")]
    [InlineData("https://example.com/calendars/user/events/", "sibling")]
    [InlineData("https://example.com/calendars/user/events/", "collection")]
    [InlineData("https://user:secret@example.com/calendars/user/events/", "valid")]
    [InlineData("https://example.com/calendars/user/events/?private=true", "valid")]
    [InlineData("https://example.com/calendars/user/events/#private", "valid")]
    [InlineData("https://example.com/calendars/user/%2e%2e/events/", "valid")]
    [InlineData("ftp://example.com/calendars/user/events/", "valid")]
    public async Task CalendarMultigetRejectsUnsafePlansBeforeWireWork(string calendarHref, string resourceShape)
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            throw new InvalidOperationException("No request is expected for an unsafe plan.");
        }));
        IReadOnlyList<string> hrefs = resourceShape switch
        {
            "too_many" => Enumerable.Range(0, 51)
                .Select(index => $"https://example.com/calendars/user/events/{index}.ics")
                .ToArray(),
            "relative" => ["a.ics"],
            "nested" => ["https://example.com/calendars/user/events/nested/a.ics"],
            "sibling" => ["https://example.com/calendars/user/other/a.ics"],
            "collection" => ["https://example.com/calendars/user/events/"],
            _ => ["https://example.com/calendars/user/events/a.ics"]
        };

        var reads = await sut.GetCalendarResourcesForQueryAsync(
            calendarHref,
            hrefs,
            TestContext.Current.CancellationToken);

        reads.ShouldAllBe(read => read.Code == CalendarResourceReadCode.InvalidInput);
        requestCount.ShouldBe(0);
    }

    [Fact]
    public async Task DirectQueryGetAcceptsCalendarHrefWithoutTrailingSlashForItsDirectResource()
    {
        const string calendarHref = "https://example.com/calendars/user/events";
        const string resourceHref = "https://example.com/calendars/user/events/a.ics";
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"r1\"") },
                Content = new StringContent("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")
            };
        }));

        var read = await sut.GetCalendarResourceDirectlyForQueryAsync(
            calendarHref,
            resourceHref,
            TestContext.Current.CancellationToken);

        read.Code.ShouldBe(CalendarResourceReadCode.Success);
        requestCount.ShouldBe(1);
    }

    [Fact]
    public async Task DirectQueryGetRejectsIdentityPreservingRedirectWithoutLocation()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        const string resourceHref = "https://example.com/calendars/user/events/a.ics";
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)));

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            sut.GetCalendarResourceDirectlyForQueryAsync(
                calendarHref,
                resourceHref,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://foreign.example/calendars/user/events/", "https://example.com/calendars/user/events/a.ics")]
    [InlineData("https://example.com/calendars/user/events/", "a.ics")]
    [InlineData("https://example.com/calendars/user/events/", "https://example.com/calendars/user/events/nested/a.ics")]
    [InlineData("https://example.com/calendars/user/events/", "https://user:secret@example.com/calendars/user/events/a.ics")]
    [InlineData("https://example.com/calendars/user/events/", "https://example.com/calendars/user/events/a.ics?private=true")]
    [InlineData("https://example.com/calendars/user/events/", "https://example.com/calendars/user/events/a.ics#private")]
    [InlineData("https://example.com/calendars/user/events/", "https://example.com/calendars/user/events/nested%2Fa.ics")]
    [InlineData("https://example.com/calendars/user/events/", "https://example.com/calendars/user/events/nested%5Ca.ics")]
    [InlineData("https://example.com/calendars/user/events/", "https://example.com/calendars/user/events/%2e%2e/a.ics")]
    [InlineData("https://example.com/calendars/user/events/", "HTTP://EXAMPLE.COM/calendars/user/events/a.ics")]
    [InlineData("https://example.com/calendars/user/events/", "ftp://example.com/calendars/user/events/a.ics")]
    public async Task DirectQueryGetRejectsUnsafeInitialIdentityBeforeWireWork(
        string calendarHref,
        string resourceHref)
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            throw new InvalidOperationException("No request is expected for an unsafe identity.");
        }));

        var read = await sut.GetCalendarResourceDirectlyForQueryAsync(
            calendarHref,
            resourceHref,
            TestContext.Current.CancellationToken);

        read.Code.ShouldBe(CalendarResourceReadCode.InvalidInput);
        requestCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("https://example.com/calendars/user/other/a.ics")]
    [InlineData("https://example.com/calendars/user/events/b.ics")]
    [InlineData("https://foreign.example/calendars/user/events/a.ics")]
    [InlineData("https://example.com/calendars/user/events/%2e%2e/a.ics")]
    public async Task DirectQueryGetRejectsRedirectThatChangesAuthorizedResourceIdentity(string location)
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        const string resourceHref = calendarHref + "a.ics";
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri(location) }
            };
        }));

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            sut.GetCalendarResourceDirectlyForQueryAsync(
                calendarHref,
                resourceHref,
                TestContext.Current.CancellationToken));

        requestCount.ShouldBe(1);
    }

    [Fact]
    public async Task DirectQueryGetPreservesPurposeAcrossIdentityPreservingRedirect()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        const string resourceHref = calendarHref + "a.ics";
        var requestCount = 0;
        var marked = new List<bool>();
        var sut = CreateSut(new StubHttpMessageHandler(request =>
        {
            requestCount++;
            marked.Add(request.Options.TryGetValue(CalendarHttpTelemetry.RequestPurposeKey, out var purpose)
                && purpose == CalendarHttpTelemetry.QueryResourceRead);
            return requestCount == 1
                ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri(resourceHref) }
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Headers = { ETag = new EntityTagHeaderValue("\"r1\"") },
                    Content = new StringContent("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")
                };
        }));
        var meter = new CalendarDirectGetBudget().StartResource();
        using var scope = CalendarHttpTelemetry.BeginQueryResourceRead(meter);

        var result = await sut.GetCalendarResourceDirectlyForQueryAsync(
            calendarHref,
            resourceHref,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        requestCount.ShouldBe(2);
        marked.ShouldAllBe(value => value);
    }

    [Fact]
    public async Task GetCalendarResourceForQueryAsync_RetainsUnavailableMultigetPerCalendarUntilRediscovery()
    {
        const string firstCalendar = "https://example.com/calendars/user/events/";
        const string secondCalendar = "https://example.com/calendars/user/other/";
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }));

        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
            sut.GetCalendarResourceForQueryAsync(firstCalendar, firstCalendar + "a.ics", CancellationToken.None));
        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
            sut.GetCalendarResourceForQueryAsync(firstCalendar, firstCalendar + "a.ics", CancellationToken.None));
        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
            sut.GetCalendarResourceForQueryAsync(secondCalendar, secondCalendar + "a.ics", CancellationToken.None));
        requestCount.ShouldBe(2);

        sut.RediscoverCapabilities();
        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
            sut.GetCalendarResourceForQueryAsync(firstCalendar, firstCalendar + "a.ics", CancellationToken.None));
        requestCount.ShouldBe(3);
    }

    [Fact]
    public async Task CalendarMultigetStaleInflightUnsupportedCannotRepopulateAfterRediscovery()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        const string resourceHref = calendarHref + "a.ics";
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var sut = CreateSut(new AsyncStubHttpMessageHandler(async _ =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                requestStarted.TrySetResult();
                await releaseResponse.Task;
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }));
        var transport = sut;

        var first = transport.GetCalendarResourcesForQueryAsync(calendarHref, [resourceHref], TestContext.Current.CancellationToken);
        await requestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        sut.RediscoverCapabilities();
        releaseResponse.TrySetResult();
        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() => first);
        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() => transport.GetCalendarResourcesForQueryAsync(
            calendarHref,
            [resourceHref],
            TestContext.Current.CancellationToken));

        requestCount.ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed, "")]
    [InlineData(HttpStatusCode.NotImplemented, "")]
    [InlineData(HttpStatusCode.BadRequest, "<d:error xmlns:d='DAV:'><d:supported-report/></d:error>")]
    [InlineData(HttpStatusCode.Forbidden, "<d:error xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'><c:supported-calendar-data/></d:error>")]
    public async Task CalendarMultigetCachesOnlyClosedVerifiedUnavailableOutcomes(
        HttpStatusCode status,
        string body)
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }));

        for (var call = 0; call < 2; call++)
        {
            await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
                sut.GetCalendarResourceForQueryAsync(
                    calendarHref,
                    calendarHref + "a.ics",
                    TestContext.Current.CancellationToken));
        }

        requestCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CalendarMultigetNeverCachesGenericOrTransientReportFailure(HttpStatusCode status)
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("<d:error xmlns:d='DAV:'><d:need-privileges/></d:error>")
            };
        }));

        for (var call = 0; call < 2; call++)
        {
            await Should.ThrowAsync<HttpRequestException>(() => sut.GetCalendarResourceForQueryAsync(
                calendarHref,
                calendarHref + "a.ics",
                TestContext.Current.CancellationToken));
        }

        requestCount.ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task CalendarMultigetRejectsInvalidUtf8ErrorWithoutCachingFallback(HttpStatusCode status)
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent([0xc3, 0x28])
            };
        }));

        for (var call = 0; call < 2; call++)
        {
            await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
                sut.GetCalendarResourceForQueryAsync(
                    calendarHref,
                    calendarHref + "a.ics",
                    TestContext.Current.CancellationToken));
        }

        requestCount.ShouldBe(2);
    }

    [Theory]
    [InlineData("<root xmlns:d='DAV:'><d:supported-report/></root>")]
    [InlineData("<d:error xmlns:d='DAV:'><wrapper><d:supported-report/></wrapper></d:error>")]
    [InlineData("<d:multistatus xmlns:d='DAV:'><d:response><d:supported-report/></d:response></d:multistatus>")]
    [InlineData("<d:error xmlns:d='DAV:'><d:supported-method/></d:error>")]
    [InlineData("<d:error xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'><c:supported-calendar-component/></d:error>")]
    public async Task CalendarMultigetDoesNotTreatEmbeddedUnsupportedNameAsDavError(string body)
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body) };
        }));

        for (var call = 0; call < 2; call++)
        {
            await Should.ThrowAsync<HttpRequestException>(() => sut.GetCalendarResourceForQueryAsync(
                calendarHref,
                calendarHref + "a.ics",
                TestContext.Current.CancellationToken));
        }

        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task CalendarMultigetMalformedDavErrorDoesNotEnableFallback()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<d:error xmlns:d='DAV:'><d:supported-report>")
            };
        }));

        for (var call = 0; call < 2; call++)
        {
            await Should.ThrowAsync<HttpRequestException>(() => sut.GetCalendarResourceForQueryAsync(
                calendarHref,
                calendarHref + "a.ics",
                TestContext.Current.CancellationToken));
        }

        requestCount.ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CalendarMultigetCombinesComplementarySuccessfulPropstatsIndependentOfOrder(bool reverse)
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        const string resourceHref = calendarHref + "a.ics";
        const string etag = "<d:propstat><d:prop><d:getetag>&quot;r1&quot;</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>";
        const string data = "<d:propstat><d:prop><c:calendar-data>BEGIN:VCALENDAR\nEND:VCALENDAR\n</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>";
        var properties = reverse ? data + etag : etag + data;
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(
                $"<d:multistatus xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'><d:response><d:href>/calendars/user/events/a.ics</d:href>{properties}</d:response></d:multistatus>")
        }));

        var result = await sut.GetCalendarResourceForQueryAsync(
            calendarHref,
            resourceHref,
            TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        result.EntityTag.ShouldBe("\"r1\"");
        Encoding.UTF8.GetString(result.AuthoritativeUtf8.Span).ShouldBe("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
    }

    [Fact]
    public async Task CalendarMultigetRejectsDuplicateSuccessfulPropertyTruth()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent("""
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:response>
                  <d:href>/calendars/user/events/a.ics</d:href>
                  <d:propstat><d:prop><d:getetag>"r1"</d:getetag><c:calendar-data>one</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                  <d:propstat><d:prop><d:getetag>"r2"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                </d:response></d:multistatus>
                """)
        }));

        await Should.ThrowAsync<System.Xml.XmlException>(() => sut.GetCalendarResourceForQueryAsync(
            calendarHref,
            calendarHref + "a.ics",
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("<root xmlns:d='DAV:'><d:response><d:href>/calendars/user/events/a.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response></root>")]
    [InlineData("<d:multistatus xmlns:d='DAV:'><wrapper><d:response><d:href>/calendars/user/events/a.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response></wrapper></d:multistatus>")]
    [InlineData("<d:multistatus xmlns:d='DAV:'><d:response><d:href>/calendars/user/events/a.ics</d:href><d:propstat><d:prop/><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat><d:propstat><d:prop/><d:status>HTTP/1.1 500 Server Error</d:status></d:propstat></d:response></d:multistatus>")]
    public async Task CalendarMultigetRejectsNonCanonicalEnvelopeAndInconsistentFailureTruth(string responseBody)
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(responseBody)
        }));

        await Should.ThrowAsync<System.Xml.XmlException>(() => sut.GetCalendarResourceForQueryAsync(
            calendarHref,
            calendarHref + "a.ics",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CalendarMultigetRejectsResponseNestedBeyondDepthBound()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var opening = string.Concat(Enumerable.Range(0, 65).Select(index => $"<x:n{index}>"));
        var closing = string.Concat(Enumerable.Range(0, 65).Reverse().Select(index => $"</x:n{index}>"));
        var body = $"<d:multistatus xmlns:d='DAV:' xmlns:x='urn:test'><d:response>{opening}{closing}</d:response></d:multistatus>";
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(body)
        }));

        await Should.ThrowAsync<System.Xml.XmlException>(() => sut.GetCalendarResourceForQueryAsync(
            calendarHref,
            calendarHref + "a.ics",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CalendarMultigetRejectsMarkupOnlyResponseEnvelopeAboveBound()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var body = "<d:multistatus xmlns:d='DAV:' xmlns:x='urn:test'><d:response>"
            + $"<x:marker data='{new string('a', (64 * 1024) + 1)}'/></d:response></d:multistatus>";
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(body)
        }));

        await Should.ThrowAsync<System.Xml.XmlException>(() => sut.GetCalendarResourceForQueryAsync(
            calendarHref,
            calendarHref + "a.ics",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CalendarMultigetRejectsCommentAboveResponseEnvelopeBound()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var body = "<d:multistatus xmlns:d='DAV:'><d:response><!--"
            + new string('a', (64 * 1024) + 1)
            + "--></d:response></d:multistatus>";
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(body)
        }));

        await Should.ThrowAsync<System.Xml.XmlException>(() => sut.GetCalendarResourceForQueryAsync(
            calendarHref,
            calendarHref + "a.ics",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CalendarMultigetRejectsASecondResponseBeforeParsingItWhenOneWasRequested()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var secondPayload = new string('x', 4 * 1024 * 1024);
        var body = $"""
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response><d:href>/calendars/user/events/a.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>
              <d:response><d:href>/calendars/user/events/b.ics</d:href><d:propstat><d:prop><c:calendar-data>{secondPayload}</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            </d:multistatus>
            """;
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(body)
        }));

        await Should.ThrowAsync<System.Xml.XmlException>(() => sut.GetCalendarResourceForQueryAsync(
            calendarHref,
            calendarHref + "a.ics",
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("<d:response xmlns:d='DAV:'><d:href>/calendars/user/events/a.ics</d:href><d:href>/calendars/user/events/a.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>")]
    [InlineData("<d:response xmlns:d='DAV:'><d:href>/calendars/user/events/a.ics</d:href><d:propstat><d:prop/><d:prop/><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response>")]
    [InlineData("<d:response xmlns:d='DAV:'><d:href>/calendars/user/events/a.ics</d:href><d:propstat><d:prop/></d:propstat></d:response>")]
    public async Task CalendarMultigetRejectsDuplicateOrIncompleteStructuralTruth(string response)
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent($"<d:multistatus xmlns:d='DAV:'>{response}</d:multistatus>")
        }));

        await Should.ThrowAsync<System.Xml.XmlException>(() => sut.GetCalendarResourceForQueryAsync(
            calendarHref,
            calendarHref + "a.ics",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CalendarMultigetRejectsUtf16EvenWithMatchingBomAndDeclaration()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        const string xml = "<?xml version='1.0' encoding='utf-16'?><d:multistatus xmlns:d='DAV:'><d:response><d:href>/calendars/user/events/a.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response></d:multistatus>";
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new ByteArrayContent(bytes)
        }));

        await Should.ThrowAsync<System.Xml.XmlException>(() => sut.GetCalendarResourceForQueryAsync(
            calendarHref,
            calendarHref + "a.ics",
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://foreign.example/calendars/user/events/a.ics")]
    [InlineData("/calendars/user/events/nested/a.ics")]
    [InlineData("/calendars/user/events/../outside.ics")]
    public async Task GetCalendarResourceForQueryAsync_RejectsUnsafeMultigetResponseBeforeVerification(string returnedHref)
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        const string resourceHref = calendarHref + "a.ics";
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    $"<d:multistatus xmlns:d='DAV:'><d:response><d:href>{returnedHref}</d:href><d:propstat><d:prop><d:getetag>\"r1\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>")
            };
        }));

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            sut.GetCalendarResourceForQueryAsync(calendarHref, resourceHref, CancellationToken.None));
        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            sut.GetCalendarResourceForQueryAsync(calendarHref, resourceHref, CancellationToken.None));

        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task MutationCapabilities_AreRetainedAndScopedByResourceAndOperation()
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }), "https://example.com/");
        var delete = new CalendarResourceDeleteRequest(
            "https://example.com/calendars/user/events/a.ics",
            "\"r1\"");

        (await sut.DeleteCalendarResourceAsync(delete, CancellationToken.None)).Code
            .ShouldBe(CalendarResourceDeleteDispatchCode.UnsupportedCapability);
        (await sut.DeleteCalendarResourceAsync(delete, CancellationToken.None)).Code
            .ShouldBe(CalendarResourceDeleteDispatchCode.UnsupportedCapability);
        (await sut.UpdateCalendarResourceAsync(
            new CalendarResourceUpdateRequest(delete.ResourceHref, delete.EntityTag, "body"u8.ToArray()),
            CancellationToken.None)).Code.ShouldBe(CalendarResourceUpdateDispatchCode.UnsupportedCapability);
        (await sut.DeleteCalendarResourceAsync(
            delete with { ResourceHref = "https://example.com/calendars/user/events/b.ics" },
            CancellationToken.None)).Code.ShouldBe(CalendarResourceDeleteDispatchCode.UnsupportedCapability);

        requestCount.ShouldBe(3);
    }

    [Fact]
    public async Task MutationCapabilities_DavUnsupportedErrorIsRetainedForEveryOperationWithoutRedispatch()
    {
        const string calendarHref = "https://example.com/calendars/user/events/";
        const string sourceHref = calendarHref + "a.ics";
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "<d:error xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><c:supported-calendar-component/></d:error>",
                    Encoding.UTF8,
                    "application/xml")
            };
        }), "https://example.com/");
        var create = new CalendarResourceCreateRequest(calendarHref, calendarHref + "created.ics", "body"u8.ToArray());
        var delete = new CalendarResourceDeleteRequest(sourceHref, "\"r1\"");
        var update = new CalendarResourceUpdateRequest(sourceHref, "\"r1\"", "body"u8.ToArray());
        var move = new CalendarResourceMoveDispatchRequest(sourceHref, calendarHref + "moved.ics", "\"r1\"");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            (await sut.CreateCalendarResourceAsync(create, CancellationToken.None)).Code
                .ShouldBe(CalendarResourceCreateCode.UnsupportedCapability);
            (await sut.DeleteCalendarResourceAsync(delete, CancellationToken.None)).Code
                .ShouldBe(CalendarResourceDeleteDispatchCode.UnsupportedCapability);
            (await sut.UpdateCalendarResourceAsync(update, CancellationToken.None)).Code
                .ShouldBe(CalendarResourceUpdateDispatchCode.UnsupportedCapability);
            (await sut.MoveCalendarResourceAsync(move, CancellationToken.None)).Code
                .ShouldBe(CalendarResourceMoveDispatchCode.UnsupportedCapability);
        }

        requestCount.ShouldBe(4);
    }

    [Fact]
    public async Task MoveCapability_IsScopedToTheSourceAndDestinationCalendarPair()
    {
        const string sourceHref = "https://example.com/calendars/user/source/a.ics";
        const string blockedDestination = "https://example.com/calendars/user/blocked/a.ics";
        const string supportedDestination = "https://example.com/calendars/user/supported/a.ics";
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(request =>
        {
            requestCount++;
            var destination = request.Headers.GetValues("Destination").Single();
            return destination == blockedDestination
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        "<d:error xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'><c:supported-calendar-component/></d:error>")
                }
                : new HttpResponseMessage(HttpStatusCode.Created);
        }), "https://example.com/");

        var blocked = new CalendarResourceMoveDispatchRequest(sourceHref, blockedDestination, "\"r1\"");
        var supported = new CalendarResourceMoveDispatchRequest(sourceHref, supportedDestination, "\"r1\"");

        (await sut.MoveCalendarResourceAsync(blocked, CancellationToken.None)).Code
            .ShouldBe(CalendarResourceMoveDispatchCode.UnsupportedCapability);
        (await sut.MoveCalendarResourceAsync(blocked, CancellationToken.None)).Code
            .ShouldBe(CalendarResourceMoveDispatchCode.UnsupportedCapability);
        (await sut.MoveCalendarResourceAsync(supported, CancellationToken.None)).Code
            .ShouldBe(CalendarResourceMoveDispatchCode.Dispatched);

        requestCount.ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CreateCalendarResourceAsync_TransientOutcomeLeavesCapabilityUnknown(HttpStatusCode statusCode)
    {
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)));

        _ = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("transient.ics"),
            CancellationToken.None);

        var field = typeof(CalDavClient).GetField(
            "_capabilities",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var observations = field.GetValue(sut)!;
        var count = (int)observations.GetType().GetProperty("Count")!.GetValue(observations)!;
        count.ShouldBe(0);
    }

    [Fact]
    public async Task CreateCalendarResourceAsync_UsesConditionalPutWithoutSchedulingHeaders()
    {
        var body = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        var requestCount = 0;
        var handler = new AsyncStubHttpMessageHandler(async request =>
        {
            requestCount++;
            request.Method.ShouldBe(HttpMethod.Put);
            request.RequestUri!.AbsoluteUri.ShouldBe("https://example.com/calendars/user/events/new.ics");
            request.Headers.IfNoneMatch.Select(value => value.ToString()).ShouldBe(["*"]);
            request.Headers.Contains("Schedule-Reply").ShouldBeFalse();
            request.Headers.Contains("Originator").ShouldBeFalse();
            request.Headers.Contains("Recipient").ShouldBeFalse();
            request.Content!.Headers.ContentType!.ToString().ShouldBe("text/calendar; charset=utf-8");
            (await request.Content.ReadAsByteArrayAsync(CancellationToken.None)).ShouldBe(body);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var sut = CreateSut(handler);

        var result = await sut.CreateCalendarResourceAsync(
            new CalendarResourceCreateRequest(
                "https://example.com/calendars/user/events/",
                "https://example.com/calendars/user/events/new.ics",
                body),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.Dispatched);
        requestCount.ShouldBe(1);
    }

    [Fact]
    public async Task CreateCalendarResourceAsync_PreconditionFailureIsDestinationConflict()
    {
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.PreconditionFailed)));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("collision.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.DestinationConflict);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task CreateCalendarResourceAsync_NoUidConflictPreconditionIsUidConflict(HttpStatusCode statusCode)
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    "<d:error xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><c:no-uid-conflict><d:href>/private/existing.ics</d:href></c:no-uid-conflict></d:error>",
                    Encoding.UTF8,
                    "application/xml")
            };
        }));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("collision.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.UidConflict);
        result.ToString().ShouldNotContain("private");
        requestCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("<d:error xmlns:d=\"DAV:\"><d:need-privileges/></d:error>")]
    [InlineData("<c:no-uid-conflict xmlns:c=\"urn:not-caldav\"/>")]
    [InlineData("<!DOCTYPE error [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><c:no-uid-conflict xmlns:c=\"urn:ietf:params:xml:ns:caldav\">&xxe;</c:no-uid-conflict>")]
    [InlineData("not xml")]
    public async Task CreateCalendarResourceAsync_UnrelatedForbiddenRemainsForbidden(string responseBody)
    {
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/xml")
        }));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("forbidden.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.UpstreamForbidden);
    }

    [Fact]
    public async Task CreateCalendarResourceAsync_BoundsUnknownLengthForbiddenBodyBeforeParsing()
    {
        var stream = new CountingNonSeekableStream(new byte[(64 * 1024) + 8192]);
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StreamContent(stream)
        }));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("forbidden.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.UpstreamForbidden);
        stream.BytesRead.ShouldBe((64 * 1024) + 1);
    }

    [Theory]
    [InlineData("VEVENT", "private-event-marker")]
    [InlineData("VTODO", "private-todo-marker")]
    public async Task CreateCalendarResourceAsync_TransportFaultForEachEntityKindIsPossiblyDispatchedAndNeverLeaksContent(
        string component,
        string privateMarker)
    {
        var requestCount = 0;
        var sut = CreateSut(new AsyncStubHttpMessageHandler(_ =>
        {
            requestCount++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException(
                $"response ended after dispatch: {privateMarker}"));
        }));
        var body = Encoding.UTF8.GetBytes(
            $"BEGIN:VCALENDAR\r\nBEGIN:{component}\r\nSUMMARY:{privateMarker}\r\nEND:{component}\r\nEND:VCALENDAR\r\n");

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("ambiguous.ics") with { AuthoritativeUtf8 = body },
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.PossiblyDispatched);
        result.ToString().ShouldNotContain(privateMarker);
        requestCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task CreateCalendarResourceAsync_FollowsOnlySameOriginMethodPreservingRedirect(HttpStatusCode redirectStatus)
    {
        var requestCount = 0;
        var handler = new AsyncStubHttpMessageHandler(async request =>
        {
            requestCount++;
            request.Method.ShouldBe(HttpMethod.Put);
            request.Headers.IfNoneMatch.Select(value => value.ToString()).ShouldBe(["*"]);
            (await request.Content!.ReadAsStringAsync(CancellationToken.None)).ShouldContain("BEGIN:VCALENDAR");
            if (requestCount == 1)
            {
                return new HttpResponseMessage(redirectStatus)
                {
                    Headers = { Location = new Uri("https://example.com/calendars/user/events/canonical.ics") }
                };
            }
            request.RequestUri!.AbsoluteUri.ShouldBe("https://example.com/calendars/user/events/canonical.ics");
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var sut = CreateSut(handler);

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("redirected.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.Dispatched);
        result.ResourceHref.ShouldBe("https://example.com/calendars/user/events/canonical.ics");
        requestCount.ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.RedirectMethod)]
    public async Task CreateCalendarResourceAsync_RejectsNonMethodPreservingRedirect(HttpStatusCode statusCode)
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(statusCode)
            {
                Headers = { Location = new Uri("https://example.com/calendars/user/events/other.ics") }
            };
        }));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("redirect.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.UpstreamProtocolError);
        requestCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("https://other.example/calendars/user/events/other.ics")]
    [InlineData("https://example.com/calendars/user/events/other.ics?secret=true")]
    [InlineData("https://user@example.com/calendars/user/events/other.ics")]
    [InlineData("https://example.com/calendars/user/events/nested%2Fother.ics")]
    [InlineData("https://example.com/calendars/user/events/nested%5Cother.ics")]
    [InlineData("https://example.com/calendars/user/events/%2e%2e/other.ics")]
    [InlineData("https://example.com/calendars/user/events/nested/other.ics")]
    [InlineData("https://example.com/calendars/user/events/other.ics#fragment")]
    public async Task CreateCalendarResourceAsync_RejectsUnsafeMethodPreservingRedirect(string location)
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri(location) }
            };
        }));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("redirect.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.UpstreamProtocolError);
        requestCount.ShouldBe(1);
    }

    [Fact]
    public async Task CreateCalendarResourceAsync_RejectsMethodPreservingRedirectWithoutLocation()
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        }));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("redirect.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.UpstreamProtocolError);
        requestCount.ShouldBe(1);
    }

    [Fact]
    public async Task CreateCalendarResourceAsync_StopsAfterThreeMethodPreservingRedirects()
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
            {
                Headers = { Location = new Uri($"https://example.com/calendars/user/events/r{requestCount}.ics") }
            };
        }));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("redirect.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.UpstreamProtocolError);
        requestCount.ShouldBe(4);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CalendarResourceCreateCode.UpstreamUnauthorized)]
    [InlineData(HttpStatusCode.Forbidden, CalendarResourceCreateCode.UpstreamForbidden)]
    [InlineData(HttpStatusCode.NotFound, CalendarResourceCreateCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed, CalendarResourceCreateCode.UnsupportedCapability)]
    [InlineData(HttpStatusCode.NotImplemented, CalendarResourceCreateCode.UnsupportedCapability)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, CalendarResourceCreateCode.PayloadTooLarge)]
    [InlineData(HttpStatusCode.RequestTimeout, CalendarResourceCreateCode.PossiblyDispatched)]
    [InlineData(HttpStatusCode.Conflict, CalendarResourceCreateCode.Conflict)]
    [InlineData(HttpStatusCode.TooManyRequests, CalendarResourceCreateCode.UpstreamRateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, CalendarResourceCreateCode.PossiblyDispatched)]
    [InlineData(HttpStatusCode.InsufficientStorage, CalendarResourceCreateCode.UpstreamUnavailable)]
    [InlineData(HttpStatusCode.Accepted, CalendarResourceCreateCode.PossiblyDispatched)]
    [InlineData(HttpStatusCode.OK, CalendarResourceCreateCode.Dispatched)]
    [InlineData(HttpStatusCode.NoContent, CalendarResourceCreateCode.Dispatched)]
    public async Task CreateCalendarResourceAsync_MapsMutationHttpStatus(
        HttpStatusCode statusCode,
        CalendarResourceCreateCode expected)
    {
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("status.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://other.example/calendars/user/events/new.ics")]
    [InlineData("https://example.com/calendars/user/events/nested/new.ics")]
    [InlineData("https://example.com/calendars/user/events/new.ics?secret=true")]
    [InlineData("https://example.com/calendars/user/events/new.ics#fragment")]
    [InlineData("https://user@example.com/calendars/user/events/new.ics")]
    [InlineData("https://example.com/calendars/user/events/nested%2Fnew.ics")]
    [InlineData("https://example.com/calendars/user/events/nested%5Cnew.ics")]
    [InlineData("https://example.com/calendars/user/events/%2e%2e/new.ics")]
    [InlineData("ftp://example.com/calendars/user/events/new.ics")]
    [InlineData("https://example.com/calendars/user/events/")]
    public async Task CreateCalendarResourceAsync_RejectsUnsafeResourceBeforeNetwork(string resourceHref)
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.Created);
        }));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("new.ics") with { ResourceHref = resourceHref },
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.InvalidInput);
        requestCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("relative/calendar/")]
    [InlineData("ftp://example.com/calendars/user/events/")]
    [InlineData("https://other.example/calendars/user/events/")]
    [InlineData("https://example.com/calendars/user/events/?secret=true")]
    [InlineData("https://example.com/calendars/user/events/#fragment")]
    [InlineData("https://user@example.com/calendars/user/events/")]
    [InlineData("https://example.com/calendars/user/%2e%2e/events/")]
    public async Task CreateCalendarResourceAsync_RejectsUnsafeCalendarBeforeNetwork(string calendarHref)
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.Created);
        }));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("new.ics") with { CalendarHref = calendarHref },
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.InvalidInput);
        requestCount.ShouldBe(0);
    }

    [Fact]
    public async Task CreateCalendarResourceAsync_AcceptsCanonicalCalendarWithoutTrailingSlash()
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("new.ics") with
            {
                CalendarHref = "https://example.com/calendars/user/events"
            },
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.Dispatched);
        requestCount.ShouldBe(1);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_UsesMinimalBoundedReportAndCanonicalizesCandidates()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/calendars/user/events/a.ics</d:href><d:propstat><d:prop><d:getetag>\"r1\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>",
                    Encoding.UTF8,
                    "application/xml")
            };
        });
        var sut = CreateSut(handler);
        var from = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        var to = DateTimeOffset.Parse("2026-08-17T10:00:00Z");

        var result = await sut.QueryCandidateHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            from,
            to,
            CancellationToken.None);

        result.ShouldBe(["https://example.com/calendars/user/events/a.ics"]);
        captured!.Method.Method.ShouldBe("REPORT");
        captured.Headers.GetValues("Depth").ShouldBe(["1"]);
        var body = await captured.Content!.ReadAsStringAsync(CancellationToken.None);
        body.ShouldContain("name=\"VEVENT\"");
        body.ShouldContain("start=\"20260814T095959Z\"");
        body.ShouldContain("end=\"20260819T100001Z\"");
        body.ShouldNotContain("calendar-data");
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_UsesSecondResolutionSupersetForFractionalBounds()
    {
        string? body = null;
        var handler = new AsyncStubHttpMessageHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync(CancellationToken.None);
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent("<d:multistatus xmlns:d=\"DAV:\"/>")
            };
        });
        var sut = CreateSut(handler);

        await sut.QueryCandidateHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            DateTimeOffset.Parse("2026-08-16T10:00:00.1234567Z"),
            DateTimeOffset.Parse("2026-08-16T11:00:00.0000001Z"),
            CancellationToken.None);

        body.ShouldNotBeNull();
        body.ShouldContain("start=\"20260814T100000Z\"");
        body.ShouldContain("end=\"20260818T110001Z\"");
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_OmitsUnsafeNearLimitRanges()
    {
        var bodies = new List<string>();
        var handler = new AsyncStubHttpMessageHandler(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(CancellationToken.None));
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent("<d:multistatus xmlns:d=\"DAV:\"/>")
            };
        });
        var sut = CreateSut(handler);
        var minimumSafeFrom = DateTimeOffset.MinValue.AddDays(2);
        var maximumSafeTo = DateTimeOffset.MaxValue.AddDays(-2);
        (DateTimeOffset From, DateTimeOffset To)[] ranges =
        [
            (minimumSafeFrom.AddTicks(-1), minimumSafeFrom.AddHours(1)),
            (minimumSafeFrom, minimumSafeFrom.AddHours(1)),
            (maximumSafeTo.AddHours(-1), maximumSafeTo),
            (maximumSafeTo.AddHours(-1), maximumSafeTo.AddTicks(1))
        ];

        foreach (var range in ranges)
        {
            await sut.QueryCandidateHrefsAsync(
                "https://example.com/calendars/user/events/",
                CalendarEntityKind.Event,
                range.From,
                range.To,
                CancellationToken.None);
        }

        bodies.Count.ShouldBe(ranges.Length);
        bodies.ShouldAllBe(body => !body.Contains("time-range", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_FallsBackFromRejectedTimeRangeToKindOnlyReport()
    {
        var bodies = new List<string>();
        var handler = new AsyncStubHttpMessageHandler(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(CancellationToken.None));
            return bodies.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        "<d:error xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><c:supported-filter/></d:error>")
                }
                : new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>https://example.com/calendars/user/events/a.ics</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response></d:multistatus>")
                };
        });
        var sut = CreateSut(handler);

        var result = await sut.QueryCandidateHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-16T11:00:00Z"),
            CancellationToken.None);

        result.ShouldBe(["https://example.com/calendars/user/events/a.ics"]);
        bodies.Count.ShouldBe(2);
        bodies[0].ShouldContain("time-range");
        bodies[1].ShouldNotContain("time-range");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    public async Task QueryCandidateHrefsAsync_UnrelatedForbiddenPreservesHttpStatus(string responseBody)
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(responseBody)
            };
        });
        var sut = CreateSut(handler);

        var exception = await Should.ThrowAsync<HttpRequestException>(() =>
            sut.QueryCandidateHrefsAsync(
                "https://example.com/calendars/user/events/",
                CalendarEntityKind.Event,
                DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T11:00:00Z"),
                CancellationToken.None));

        exception.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        requestCount.ShouldBe(1);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_MandatoryMinimalReportUnsupportedFailsCapability()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "<d:error xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><c:supported-filter/></d:error>")
        });
        var sut = CreateSut(handler);

        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
            sut.QueryCandidateHrefsAsync(
                "https://example.com/calendars/user/events/",
                CalendarEntityKind.Event,
                null,
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_FallbackMinimalReportUnsupportedFailsCapability()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "<d:error xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><c:supported-filter/></d:error>")
            };
        });
        var sut = CreateSut(handler);

        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
            sut.QueryCandidateHrefsAsync(
                "https://example.com/calendars/user/events/",
                CalendarEntityKind.Event,
                DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T11:00:00Z"),
                CancellationToken.None));

        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_UnavailableMinimalCapabilityIsRetainedWithoutAnotherRequest()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        });
        var sut = CreateSut(handler);

        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
            sut.QueryCandidateHrefsAsync(
                "https://example.com/calendars/user/events/",
                CalendarEntityKind.Event,
                null,
                null,
                CancellationToken.None));
        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
            sut.QueryCandidateHrefsAsync(
                "https://example.com/calendars/user/events/",
                CalendarEntityKind.Event,
                null,
                null,
                CancellationToken.None));

        requestCount.ShouldBe(1);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_TransientFailureDoesNotDowngradeCapability()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return requestCount == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent("<d:multistatus xmlns:d=\"DAV:\" />")
                };
        });
        var sut = CreateSut(handler);

        await Should.ThrowAsync<HttpRequestException>(() =>
            sut.QueryCandidateHrefsAsync(
                "https://example.com/calendars/user/events/",
                CalendarEntityKind.Event,
                null,
                null,
                CancellationToken.None));
        var result = await sut.QueryCandidateHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None);

        result.ShouldBeEmpty();
        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_ConfigurationChangeInvalidatesUnavailableCapability()
    {
        var requestCount = 0;
        var options = new CalDavOptions { BaseUrl = "https://example.com/", Username = "first" };
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.NotImplemented);
        });
        var sut = CreateSut(handler, options);

        await AssertMinimalQueryUnsupportedAsync(sut, "https://example.com/calendars/user/events/");
        options.Username = "second";
        await AssertMinimalQueryUnsupportedAsync(sut, "https://example.com/calendars/user/events/");

        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_ExplicitRediscoveryInvalidatesUnavailableCapability()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        });
        var sut = CreateSut(handler);

        await AssertMinimalQueryUnsupportedAsync(sut, "https://example.com/calendars/user/events/");
        sut.RediscoverCapabilities();
        await AssertMinimalQueryUnsupportedAsync(sut, "https://example.com/calendars/user/events/");

        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_StaleInFlightObservationCannotRepopulateAfterRediscovery()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var handler = new AsyncStubHttpMessageHandler(async _ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                requestStarted.SetResult();
                await releaseResponse.Task;
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        });
        var sut = CreateSut(handler);

        var first = AssertMinimalQueryUnsupportedAsync(sut, "https://example.com/calendars/user/events/");
        await requestStarted.Task;
        sut.RediscoverCapabilities();
        releaseResponse.SetResult();
        await first;
        await AssertMinimalQueryUnsupportedAsync(sut, "https://example.com/calendars/user/events/");

        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_CapabilityKeysSeparateCalendarAndOperation()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        });
        var sut = CreateSut(handler, "https://example.com/");

        await AssertMinimalQueryUnsupportedAsync(sut, "https://example.com/calendars/user/events/");
        await AssertMinimalQueryUnsupportedAsync(sut, "https://example.com/calendars/user/todos/");
        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
            sut.QueryCandidateHrefsAsync(
                "https://example.com/calendars/user/events/",
                CalendarEntityKind.Todo,
                null,
                null,
                CancellationToken.None));

        requestCount.ShouldBe(3);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_IgnoresSuccessfulCollectionSelfResponse()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(
                "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/calendars/user/events/</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response><d:response><d:href>/calendars/user/events/a.ics</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response></d:multistatus>",
                Encoding.UTF8,
                "application/xml")
        });
        var sut = CreateSut(handler);

        var result = await sut.QueryCandidateHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None);

        result.ShouldBe(["https://example.com/calendars/user/events/a.ics"]);
    }

    [Theory]
    [InlineData("https://other.example/calendars/user/events/a.ics")]
    [InlineData("/calendars/user/events%2Fprivate/a.ics")]
    [InlineData("/calendars/user/events/%2e%2e/private/a.ics")]
    [InlineData("/calendars/user/events%5cprivate/a.ics")]
    [InlineData("/calendars/user/events/%2e/a.ics")]
    [InlineData("/calendars/user/events/%2E%2E/a.ics")]
    [InlineData("/calendars/user/events/%2e%2E/private.ics")]
    public async Task QueryCandidateHrefsAsync_RejectsUnsafeReportCandidateBeforeAnyGet(string candidateHref)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    $"<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>{candidateHref}</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response></d:multistatus>",
                    Encoding.UTF8,
                    "application/xml")
            };
        });
        var sut = CreateSut(handler);

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryCandidateHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None));

        requests.ShouldHaveSingleItem().Method.Method.ShouldBe("REPORT");
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_RejectsCrossOriginCalendarBeforeNetwork()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.MultiStatus);
        }));

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryCandidateHrefsAsync(
            "https://other.example/calendars/user/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None));

        requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_RejectsSeeOtherWithoutFollowing()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.RedirectMethod)
            {
                Headers = { Location = new Uri("/calendars/user/events/other/", UriKind.Relative) }
            };
        }));

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryCandidateHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None));

        requests.ShouldHaveSingleItem().Method.Method.ShouldBe("REPORT");
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task QueryCandidateHrefsAsync_FollowsRedirectButRejectsCandidateOutsideAuthorizedCalendarIdentity(
        HttpStatusCode statusCode)
    {
        var requests = new List<(HttpMethod Method, string Uri, string Body)>();
        var handler = new AsyncStubHttpMessageHandler(async request =>
        {
            requests.Add((
                request.Method,
                request.RequestUri!.AbsoluteUri,
                await request.Content!.ReadAsStringAsync(CancellationToken.None)));
            if (requests.Count == 1)
            {
                return new HttpResponseMessage(statusCode)
                {
                    Headers = { Location = new Uri("/calendars/user/redirected/", UriKind.Relative) }
                };
            }
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>a.ics</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response></d:multistatus>")
            };
        });
        var sut = CreateSut(handler);

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryCandidateHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None));

        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(request => request.Method.Method == "REPORT");
        requests[1].Uri.ShouldBe("https://example.com/calendars/user/redirected/");
        requests[1].Body.ShouldBe(requests[0].Body);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_RejectsOversizedContentLength()
    {
        var content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]);
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = content
        }));

        var exception = await Should.ThrowAsync<HttpRequestException>(() => sut.QueryCandidateHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None));

        exception.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task QueryCandidateHrefsAsync_StopsUnknownLengthBodyAtLimitPlusOne()
    {
        var stream = new CountingNonSeekableStream(new byte[4 * 1024 * 1024 + 8192]);
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StreamContent(stream)
        }));

        var exception = await Should.ThrowAsync<HttpRequestException>(() => sut.QueryCandidateHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None));

        exception.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        stream.BytesRead.ShouldBe(4 * 1024 * 1024 + 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak-revision\"")]
    public async Task GetCalendarResourceAsync_ReturnsConcurrencyUnavailableForMissingOrWeakEntityTag(string? entityTag)
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", Encoding.UTF8, "text/calendar")
            };
            if (entityTag is not null)
                response.Headers.ETag = EntityTagHeaderValue.Parse(entityTag);
            return response;
        });
        var sut = CreateSut(handler);

        var result = await sut.GetCalendarResourceAsync(
            "https://example.com/calendars/user/events/a.ics",
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.ConcurrencyUnavailable);
        result.EntityTag.ShouldBeNull();
        Encoding.UTF8.GetString(result.AuthoritativeUtf8.Span).ShouldBe(
            "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        result.Snapshot.ShouldBeNull();
    }

    [Fact]
    public async Task ProbeCalendarResourceAbsenceAsync_MarksOnlyTheExplicitWireRequest()
    {
        var purposes = new List<string?>();
        var handler = new StubHttpMessageHandler(request =>
        {
            purposes.Add(request.Options.TryGetValue(
                CalendarHttpTelemetry.RequestPurposeKey,
                out var purpose)
                    ? purpose
                    : null);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var sut = CreateSut(handler);

        var probe = await sut.ProbeCalendarResourceAbsenceAsync(
            "https://example.com/calendars/user/events/probe.ics",
            CancellationToken.None);
        var ordinary = await sut.GetCalendarResourceAsync(
            "https://example.com/calendars/user/events/ordinary.ics",
            CancellationToken.None);

        probe.Code.ShouldBe(CalendarResourceReadCode.NotFound);
        ordinary.Code.ShouldBe(CalendarResourceReadCode.NotFound);
        purposes.ShouldBe([CalendarHttpTelemetry.AbsenceProbe, null]);
    }

    [Fact]
    public async Task MovePresenceProbe_UsesHeadersOnlyAndMarksExpectedAbsencePurpose()
    {
        string? purpose = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            _ = request.Options.TryGetValue(CalendarHttpTelemetry.RequestPurposeKey, out purpose);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"private-tag\"") },
                Content = new StringContent("private Calendar content that must not be retained")
            };
        });
        var sut = CreateSut(handler);

        var result = await ((ICalendarMoveResourceTransport)sut).ProbeMoveResourcePresenceAsync(
            "https://example.com/calendars/user/events/",
            "https://example.com/calendars/user/events/present.ics",
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        result.AuthoritativeUtf8.IsEmpty.ShouldBeTrue();
        result.EntityTag.ShouldBeNull();
        purpose.ShouldBe(CalendarHttpTelemetry.AbsenceProbe);
    }

    [Fact]
    public void MovePresenceTransport_HasNoUnscopedProductionSeam()
    {
        typeof(CalDavClient).Assembly.GetType(
                "DotnetAgents.CalDav.Core.Internal.ICalendarResourcePresenceTransport")
            .ShouldBeNull();
        typeof(CalDavClient).GetMethod(
                "ProbeCalendarResourcePresenceAsync",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .ShouldBeNull();
    }

    [Fact]
    public async Task ProbeCalendarResourceAbsenceAsync_PreservesPurposeThroughDecoratorDefault()
    {
        string? purpose = null;
        var concrete = CreateSut(new StubHttpMessageHandler(request =>
        {
            _ = request.Options.TryGetValue(CalendarHttpTelemetry.RequestPurposeKey, out purpose);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        ICalendarClient decorator = new GetOnlyCalendarClient(concrete);

        var result = await decorator.ProbeCalendarResourceAbsenceAsync(
            "https://example.com/calendars/user/events/probe.ics",
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.NotFound);
        purpose.ShouldBe(CalendarHttpTelemetry.AbsenceProbe);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task GetCalendarResourceAsync_FollowsBoundedSameOriginReadRedirects(HttpStatusCode statusCode)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
            [
                new HttpResponseMessage(statusCode)
                {
                    Headers = { Location = new Uri("/redirected/calendars/current.ics", UriKind.Relative) }
                },
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Headers = { ETag = new EntityTagHeaderValue("\"revision-2\"") },
                    Content = new StringContent("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", Encoding.UTF8, "text/calendar")
                }
            ],
            requests);
        var sut = CreateSut(handler);

        var result = await sut.GetCalendarResourceAsync(
            "https://example.com/calendars/user/events/old.ics",
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        result.ResourceHref.ShouldBe("https://example.com/calendars/user/events/old.ics");
        Encoding.UTF8.GetString(result.AuthoritativeUtf8.Span)
            .ShouldBe("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(request => request.Method == HttpMethod.Get);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveResourceRead_RejectsSameOriginRedirectOutsideAuthorizedCalendar(
        bool absenceProbe)
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("/calendars/user/archive/private.ics", UriKind.Relative) }
            };
        }));

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            ((ICalendarMoveResourceTransport)sut).ReadMoveResourceAsync(
                "https://example.com/calendars/user/events/",
                "https://example.com/calendars/user/events/a.ics",
                absenceProbe,
                CancellationToken.None));

        requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MovePresenceProbe_RejectsSameOriginRedirectOutsideAuthorizedCalendar()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
            {
                Headers = { Location = new Uri("/calendars/user/private/a.ics", UriKind.Relative) }
            };
        }));

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            ((ICalendarMoveResourceTransport)sut).ProbeMoveResourcePresenceAsync(
                "https://example.com/calendars/user/events/",
                "https://example.com/calendars/user/events/a.ics",
                CancellationToken.None));

        requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MoveResourceRead_AcceptsCanonicalRedirectWithinAuthorizedCalendar()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
            [
                new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
                {
                    Headers = { Location = new Uri("canonical.ics", UriKind.Relative) }
                },
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Headers = { ETag = new EntityTagHeaderValue("\"revision-2\"") },
                    Content = new StringContent(
                        "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n",
                        Encoding.UTF8,
                        "text/calendar")
                }
            ],
            requests);
        var sut = CreateSut(handler);

        var result = await ((ICalendarMoveResourceTransport)sut).ReadMoveResourceAsync(
            "https://example.com/calendars/user/events/",
            "https://example.com/calendars/user/events/old.ics",
            absenceProbe: false,
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.Success);
        requests.Select(request => request.RequestUri!.AbsoluteUri).ShouldBe([
            "https://example.com/calendars/user/events/old.ics",
            "https://example.com/calendars/user/events/canonical.ics"
        ]);
    }

    [Theory]
    [InlineData("https://other.example/calendars/user/events/a.ics")]
    [InlineData("/calendars/user/events%2Fprivate/a.ics")]
    [InlineData("/calendars/user/events/a.ics?secret=true")]
    [InlineData("https://user:secret@example.com/calendars/user/events/a.ics")]
    [InlineData("/calendars/user/events/a.ics#fragment")]
    public async Task GetCalendarResourceAsync_RejectsUnsafeRedirectWithoutFollowingIt(string location)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
            {
                Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) }
            };
        });
        var sut = CreateSut(handler);

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.GetCalendarResourceAsync(
            "https://example.com/calendars/user/events/a.ics",
            CancellationToken.None));

        requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetCalendarResourceAsync_RejectsSeeOtherWithoutFollowingIt()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.RedirectMethod)
            {
                Headers = { Location = new Uri("/calendars/user/events/other.ics", UriKind.Relative) }
            };
        });
        var sut = CreateSut(handler);

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.GetCalendarResourceAsync(
            "https://example.com/calendars/user/events/a.ics",
            CancellationToken.None));

        requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetCalendarResourceAsync_RejectsInvalidUtf8WithoutReturningAResource()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { ETag = new EntityTagHeaderValue("\"revision-1\"") },
            Content = new ByteArrayContent([0x43, 0xC3, 0x28])
        });
        var sut = CreateSut(handler);

        var result = await sut.GetCalendarResourceAsync(
            "https://example.com/calendars/user/events/a.ics",
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.UpstreamProtocolError);
        result.EntityTag.ShouldBeNull();
        result.AuthoritativeUtf8.IsEmpty.ShouldBeTrue();
        result.Snapshot.ShouldBeNull();
    }

    [Theory]
    [InlineData((4 * 1024 * 1024) - 1, CalendarResourceReadCode.Success)]
    [InlineData(4 * 1024 * 1024, CalendarResourceReadCode.Success)]
    [InlineData((4 * 1024 * 1024) + 1, CalendarResourceReadCode.PayloadTooLarge)]
    public async Task GetCalendarResourceAsync_EnforcesDecompressedUtf8LimitPlusOne(
        int byteCount,
        CalendarResourceReadCode expectedCode)
    {
        var payload = Enumerable.Repeat((byte)'A', byteCount).ToArray();
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            await gzip.WriteAsync(payload, TestContext.Current.CancellationToken);
        var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentEncoding.Add("gzip");
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { ETag = new EntityTagHeaderValue("\"revision-1\"") },
            Content = content
        });
        var sut = CreateSut(handler);

        var result = await sut.GetCalendarResourceAsync(
            "https://example.com/calendars/user/events/a.ics",
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        if (expectedCode == CalendarResourceReadCode.Success)
            result.AuthoritativeUtf8.Length.ShouldBe(byteCount);
        else
        {
            result.AuthoritativeUtf8.IsEmpty.ShouldBeTrue();
            result.Snapshot.ShouldBeNull();
            result.ObservedByteCount.ShouldBe(byteCount);
        }
    }

    [Fact]
    public async Task GetCalendarResourceAsync_StopsUnknownLengthStreamAtLimitPlusOne()
    {
        var stream = new CountingNonSeekableStream(new byte[(4 * 1024 * 1024) + 128]);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { ETag = new EntityTagHeaderValue("\"revision-1\"") },
            Content = new StreamContent(stream)
        });
        var sut = CreateSut(handler);

        var result = await sut.GetCalendarResourceAsync(
            "https://example.com/calendars/user/events/a.ics",
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.PayloadTooLarge);
        result.ObservedByteCount.ShouldBe((4 * 1024 * 1024) + 1);
        result.AuthoritativeUtf8.IsEmpty.ShouldBeTrue();
        result.Snapshot.ShouldBeNull();
        stream.BytesRead.ShouldBe((4 * 1024 * 1024) + 1);
    }

    [Theory]
    [InlineData("https://other.example/calendars/user/events/a.ics")]
    [InlineData("https://user:secret@example.com/calendars/user/events/a.ics")]
    [InlineData("https://example.com/calendars/user/events/a.ics#fragment")]
    [InlineData("https://example.com/calendars/user/events%2Fprivate/a.ics")]
    [InlineData("https://example.com/calendars/user/events%5cprivate/a.ics")]
    [InlineData("/calendars/user/events/a.ics")]
    public async Task GetCalendarResourceAsync_RejectsUnsafeHrefWithoutSendingRequest(string href)
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var sut = CreateSut(handler);

        var result = await sut.GetCalendarResourceAsync(href, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceReadCode.InvalidInput);
        requestCount.ShouldBe(0);
    }

    #region PROPFIND and REPORT Tests

    [Fact]
    public async Task GetCalendarsAsync_ConfiguredCalendarHomeIsProvedAndUsedWithoutWellKnownDiscovery()
    {
        const string configuredHome = "https://example.com/calendars/user/";
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
        [
            CreateCalendarHomeSetResponse(configuredHome),
            CreateCalendarListingResponse()
        ], requests);
        var sut = CreateSut(handler, configuredHome);

        var calendars = await sut.GetCalendarsAsync(CancellationToken.None);

        calendars.ShouldHaveSingleItem().Href.ShouldBe("https://example.com/calendars/user/events/");
        requests.Select(request => request.RequestUri!.AbsoluteUri).ShouldBe([configuredHome, configuredHome]);
        requests.Select(request => request.Headers.GetValues("Depth").Single()).ShouldBe(["0", "1"]);
    }

    [Fact]
    public async Task GetCalendarsAsync_ServerEndpointFallsBackToWellKnownWithoutRepeatingTheConfiguredProbe()
    {
        const string serverEndpoint = "https://example.com/remote.php/dav/";
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
        [
            CreateEmptyMultiStatusResponse(),
            CreateCalendarHomeSetResponse("https://example.com/calendars/user/"),
            CreateCalendarListingResponse()
        ], requests);
        var sut = CreateSut(handler, serverEndpoint);

        var calendars = await sut.GetCalendarsAsync(CancellationToken.None);

        calendars.ShouldHaveSingleItem().Href.ShouldBe("https://example.com/calendars/user/events/");
        requests.Select(request => request.RequestUri!.AbsoluteUri).ShouldBe(
        [
            serverEndpoint,
            "https://example.com/.well-known/caldav",
            "https://example.com/calendars/user/"
        ]);
    }

    [Fact]
    public async Task GetCalendarsAsync_UnverifiedServerTranscriptFollowsPrincipalToHomeExactlyOnce()
    {
        const string serverEndpoint = "https://example.com/remote.php/dav/";
        const string principalHref = "https://example.com/principals/user/";
        const string homeHref = "https://example.com/calendars/user/";
        var requests = new List<HttpRequestMessage>();
        var principalResponse = new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent($"""
                <d:multistatus xmlns:d="DAV:">
                  <d:response><d:href>/</d:href><d:propstat><d:prop><d:current-user-principal><d:href>{principalHref}</d:href></d:current-user-principal></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                </d:multistatus>
                """, Encoding.UTF8, "application/xml")
        };
        var handler = CreateSequencedHandler(
        [
            CreateEmptyMultiStatusResponse(),
            principalResponse,
            CreateCalendarHomeSetResponse(homeHref),
            CreateCalendarListingResponse()
        ], requests);
        var sut = CreateSut(handler, serverEndpoint);

        var calendars = await sut.GetCalendarsAsync(CancellationToken.None);

        calendars.ShouldHaveSingleItem().Href.ShouldBe("https://example.com/calendars/user/events/");
        requests.Select(request => request.RequestUri!.AbsoluteUri).ShouldBe([
            serverEndpoint,
            "https://example.com/.well-known/caldav",
            principalHref,
            homeHref
        ]);
        requests.Select(request => request.Headers.GetValues("Depth").Single()).ShouldBe(["0", "0", "0", "1"]);
    }

    [Fact]
    public async Task DiscoveryFailureLogsOnlySafeCodeAndPhaseWithoutConfiguredOrRawMarkers()
    {
        const string configured = "https://example.com/private-user-path/";
        var logger = new CapturingLogger<CalDavClient>();
        var handler = new StubHttpMessageHandler(_ => CreateEmptyMultiStatusResponse());
        var sut = new CalDavClient(
            new HttpClient(handler),
            Options.Create(new CalDavOptions
            {
                BaseUrl = configured,
                Username = "credential-user-sentinel",
                Password = "credential-password-sentinel"
            }),
            logger);

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            sut.GetCalendarsAsync(CancellationToken.None));

        logger.Entries.ShouldNotBeEmpty();
        foreach (var entry in logger.Entries)
            entry.Keys.ShouldAllBe(key => key == "Code" || key == "Phase" || key == "{OriginalFormat}");
        var text = string.Join('\n', logger.Entries.Select(entry => entry.Message));
        text.ShouldNotContain(configured);
        text.ShouldNotContain("private-user-path");
        text.ShouldNotContain("credential-user-sentinel");
        text.ShouldNotContain("credential-password-sentinel");
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task GetCalendarsAsync_UnsupportedServerEndpointFallsBackToWellKnown(
        HttpStatusCode endpointStatus)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
        [
            new HttpResponseMessage(endpointStatus),
            CreateCalendarHomeSetResponse("https://example.com/calendars/user/"),
            CreateCalendarListingResponse()
        ], requests);
        var sut = CreateSut(handler, "https://example.com/remote.php/dav/");

        var calendars = await sut.GetCalendarsAsync(CancellationToken.None);

        calendars.ShouldHaveSingleItem();
        requests.Select(request => request.RequestUri!.AbsoluteUri).ShouldBe(
        [
            "https://example.com/remote.php/dav/",
            "https://example.com/.well-known/caldav",
            "https://example.com/calendars/user/"
        ]);
    }

    [Fact]
    public async Task GetCalendarsAsync_ConfiguredCalendarHomeFollowsCanonicalSameOriginRedirectOnce()
    {
        const string configuredHome = "https://example.com/calendars/user";
        var requests = new List<HttpRequestMessage>();
        var redirect = new HttpResponseMessage(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("/calendars/user/", UriKind.Relative);
        var handler = CreateSequencedHandler(
        [
            redirect,
            CreateCalendarHomeSetResponse("/calendars/user/"),
            CreateCalendarListingResponse()
        ], requests);
        var sut = CreateSut(handler, configuredHome);

        var calendars = await sut.GetCalendarsAsync(CancellationToken.None);

        calendars.ShouldHaveSingleItem();
        requests.Select(request => request.RequestUri!.AbsoluteUri).ShouldBe(
        [
            "https://example.com/calendars/user/",
            "https://example.com/calendars/user/",
            "https://example.com/calendars/user/"
        ]);
        requests.ShouldAllBe(request => request.Method.Method == "PROPFIND");
    }

    [Fact]
    public async Task GetCalendarsAsync_ConfiguredCalendarHomeRejectsCrossOriginRedirectWithoutDiscoveryFallback()
    {
        var requests = new List<HttpRequestMessage>();
        var redirect = new HttpResponseMessage(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other.example/calendars/user/");
        var handler = CreateSequencedHandler([redirect], requests);
        var sut = CreateSut(handler, "https://example.com/calendars/user/");

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            sut.GetCalendarsAsync(CancellationToken.None));

        requests.ShouldHaveSingleItem();
        requests[0].RequestUri!.Host.ShouldBe("example.com");
    }

    [Fact]
    public async Task GetCalendarsAsync_ReturnsCanonicalAbsoluteHrefsForEveryCalendar()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
        [
            CreateCalendarHomeSetResponse(),
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent("""
                    <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                      <d:response><d:href>events/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                      <d:response><d:href>/calendars/user/todos/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                      <d:response><d:href>https://example.com/calendars/user/shared/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    </d:multistatus>
                    """, Encoding.UTF8, "application/xml")
            }
        ], requests);
        var sut = CreateSut(handler, "https://example.com/calendars/user/");

        var calendars = await sut.GetCalendarsAsync(CancellationToken.None);

        calendars.Select(calendar => calendar.Href).ShouldBe(
        [
            "https://example.com/calendars/user/events/",
            "https://example.com/calendars/user/shared/",
            "https://example.com/calendars/user/todos/"
        ]);
        calendars[0].EventSupport.ShouldBe(EntityKindSupport.Advertised);
        calendars[0].TodoSupport.ShouldBe(EntityKindSupport.NotAdvertised);
        requests.Select(request => request.Headers.GetValues("Depth").Single()).ShouldBe(["0", "1"]);
    }

    [Fact]
    public async Task GetCalendarsAsync_DoesNotFollowUnsafeCalendarHomeSetHref()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
        [
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent("""
                    <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                      <d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>https://other.example/calendars/user/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    </d:multistatus>
                    """, Encoding.UTF8, "application/xml")
            }
        ], requests);
        var sut = CreateSut(handler, "https://example.com/calendars/user/");

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            sut.GetCalendarsAsync(CancellationToken.None));
        requests.Count.ShouldBe(1);
        requests[0].RequestUri!.Host.ShouldBe("example.com");
    }

    [Fact]
    public async Task GetCalendarsAsync_RejectsUnsafeCalendarHrefWithoutPartialItems()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
        [
            CreateCalendarHomeSetResponse(),
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent("""
                    <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                      <d:response><d:href>/calendars/user/safe/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                      <d:response><d:href>https://other.example/calendars/user/unsafe/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    </d:multistatus>
                    """, Encoding.UTF8, "application/xml")
            }
        ], requests);
        var sut = CreateSut(handler, "https://example.com/calendars/user/");

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            sut.GetCalendarsAsync(CancellationToken.None));

        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(request => request.RequestUri!.Host == "example.com");
    }

    [Theory]
    [InlineData("https://user:secret@example.com/calendars/user/unsafe/")]
    [InlineData("/calendars/user/unsafe/#fragment")]
    public async Task GetCalendarsAsync_RejectsCredentialsAndFragmentsInCalendarHrefs(string unsafeHref)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
        [
            CreateCalendarHomeSetResponse(),
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent($"""
                    <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                      <d:response><d:href>{unsafeHref}</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    </d:multistatus>
                    """, Encoding.UTF8, "application/xml")
            }
        ], requests);
        var sut = CreateSut(handler, "https://example.com/calendars/user/");

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            sut.GetCalendarsAsync(CancellationToken.None));

        requests.Count.ShouldBe(2);
        requests.All(request => request.RequestUri!.UserInfo.Length == 0).ShouldBeTrue();
    }

    [Fact]
    public async Task GetCalendarsAsync_DoesNotFollowUnsafeCurrentUserPrincipalHref()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
        [
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent("""
                    <d:multistatus xmlns:d="DAV:">
                      <d:response><d:href>/</d:href><d:propstat><d:prop><d:current-user-principal><d:href>https://other.example/principals/user/</d:href></d:current-user-principal></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                    </d:multistatus>
                    """, Encoding.UTF8, "application/xml")
            }
        ], requests);
        var sut = CreateSut(handler, "https://example.com/remote.php/dav/");

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() =>
            sut.GetCalendarsAsync(CancellationToken.None));
        requests.Count.ShouldBe(1);
        requests.ShouldAllBe(request => request.RequestUri!.Host == "example.com");
    }

    #endregion

    #region Helper Methods

    private static CalDavClient CreateSut(HttpMessageHandler handler, string baseUrl = "https://example.com/remote.php/dav")
    {
        return CreateSut(handler, new CalDavOptions { BaseUrl = baseUrl });
    }

    private static CalDavClient CreateSut(HttpMessageHandler handler, CalDavOptions options)
    {
        var httpClient = new HttpClient(handler);

        return new CalDavClient(httpClient, Options.Create(options), Substitute.For<ILogger<CalDavClient>>());
    }

    private static async Task AssertMinimalQueryUnsupportedAsync(CalDavClient sut, string calendarHref) =>
        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
            sut.QueryCandidateHrefsAsync(
                calendarHref,
                CalendarEntityKind.Event,
                null,
                null,
                CancellationToken.None));

    private static CalendarResourceCreateRequest CreateCalendarResourceRequest(string name) => new(
        "https://example.com/calendars/user/events/",
        "https://example.com/calendars/user/events/" + name,
        Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class CountingNonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = _inner.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Async variant of <see cref="StubHttpMessageHandler"/> that supports
    /// faulted tasks via <c>Task.FromException&lt;T&gt;</c>, modelling
    /// true asynchronous HTTP failures more accurately than a synchronous throw.
    /// </summary>
    private sealed class AsyncStubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }

    /// <summary>
    /// Creates a stub handler that returns responses from a sequence in order.
    /// Captures each request for later assertion. The final response is repeated
    /// for any additional requests.
    /// </summary>
    private static StubHttpMessageHandler CreateSequencedHandler(
        List<HttpResponseMessage> responses,
        List<HttpRequestMessage> capturedRequests)
    {
        var index = 0;
        return new StubHttpMessageHandler(request =>
        {
            capturedRequests.Add(request);
            var response = responses[Math.Min(index, responses.Count - 1)];
            index++;
            return response;
        });
    }

    private static AsyncStubHttpMessageHandler CreateAsyncSequencedHandler(
        List<Func<Task<HttpResponseMessage>>> responseFactories,
        List<HttpRequestMessage> capturedRequests)
    {
        var index = 0;
        return new AsyncStubHttpMessageHandler(request =>
        {
            capturedRequests.Add(request);
            var responseFactory = responseFactories[Math.Min(index, responseFactories.Count - 1)];
            index++;
            return responseFactory();
        });
    }

    private static HttpResponseMessage CreateCalendarHomeSetResponse(
        string homeSetHref = "/calendars/user/") =>
        new(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent($"""
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response><d:href>{homeSetHref}</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>{homeSetHref}</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                </d:multistatus>
                """, Encoding.UTF8, "application/xml")
        };

    private static HttpResponseMessage CreateEmptyMultiStatusResponse() =>
        new(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(
                "<d:multistatus xmlns:d=\"DAV:\" />",
                Encoding.UTF8,
                "application/xml")
        };

    private static HttpResponseMessage CreateCalendarListingResponse() =>
        new(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent("""
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response><d:href>events/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                </d:multistatus>
                """, Encoding.UTF8, "application/xml")
        };

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        internal List<(string Message, IReadOnlyList<string> Keys)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var keys = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.Select(value => value.Key).ToArray()
                : [];
            Entries.Add((formatter(state, exception), keys));
        }

        private sealed class NullScope : IDisposable
        {
            internal static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }

    private sealed class GetOnlyCalendarClient(ICalendarClient inner) : ICalendarClient
    {
        public Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CalendarResourceRead> GetCalendarResourceAsync(
            string href,
            CancellationToken cancellationToken) => inner.GetCalendarResourceAsync(href, cancellationToken);

        public Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
            CalendarResourceCreateRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CalendarResourceDeleteDispatchResult> DeleteCalendarResourceAsync(
            CalendarResourceDeleteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    #endregion
}

internal static class CalDavClientQueryTestExtensions
{
    internal static async Task<CalendarResourceRead> GetCalendarResourceForQueryAsync(
        this CalDavClient client,
        string calendarHref,
        string resourceHref,
        CancellationToken cancellationToken) => (await client.GetCalendarResourcesForQueryAsync(
            calendarHref,
            [resourceHref],
            cancellationToken)).Single();
}
