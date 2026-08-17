using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
    public async Task CreateCalendarResourceAsync_PreconditionFailureIsDefiniteConflict()
    {
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.PreconditionFailed)));

        var result = await sut.CreateCalendarResourceAsync(
            CreateCalendarResourceRequest("collision.ics"),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceCreateCode.Conflict);
    }

    [Fact]
    public async Task CreateCalendarResourceAsync_NoUidConflictPreconditionIsDefiniteConflict()
    {
        var requestCount = 0;
        var sut = CreateSut(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
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

        result.Code.ShouldBe(CalendarResourceCreateCode.Conflict);
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
    public async Task QueryCalendarResourceHrefsAsync_UsesMinimalBoundedReportAndCanonicalizesCandidates()
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

        var result = await sut.QueryCalendarResourceHrefsAsync(
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
        body.ShouldContain("start=\"20260816T100000Z\"");
        body.ShouldContain("end=\"20260817T100000Z\"");
        body.ShouldNotContain("calendar-data");
    }

    [Fact]
    public async Task QueryCalendarResourceHrefsAsync_UsesSecondResolutionSupersetForFractionalBounds()
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

        await sut.QueryCalendarResourceHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            DateTimeOffset.Parse("2026-08-16T10:00:00.1234567Z"),
            DateTimeOffset.Parse("2026-08-16T11:00:00.0000001Z"),
            CancellationToken.None);

        body.ShouldNotBeNull();
        body.ShouldContain("start=\"20260816T100000Z\"");
        body.ShouldContain("end=\"20260816T110001Z\"");
    }

    [Fact]
    public async Task QueryCalendarResourceHrefsAsync_FallsBackFromRejectedTimeRangeToKindOnlyReport()
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

        var result = await sut.QueryCalendarResourceHrefsAsync(
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
    public async Task QueryCalendarResourceHrefsAsync_UnrelatedForbiddenPreservesHttpStatus(string responseBody)
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
            sut.QueryCalendarResourceHrefsAsync(
                "https://example.com/calendars/user/events/",
                CalendarEntityKind.Event,
                DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T11:00:00Z"),
                CancellationToken.None));

        exception.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        requestCount.ShouldBe(1);
    }

    [Fact]
    public async Task QueryCalendarResourceHrefsAsync_MandatoryMinimalReportUnsupportedFailsCapability()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "<d:error xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><c:supported-filter/></d:error>")
        });
        var sut = CreateSut(handler);

        await Should.ThrowAsync<CalendarDiscoveryUnsupportedCapabilityException>(() =>
            sut.QueryCalendarResourceHrefsAsync(
                "https://example.com/calendars/user/events/",
                CalendarEntityKind.Event,
                null,
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task QueryCalendarResourceHrefsAsync_FallbackMinimalReportUnsupportedFailsCapability()
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
            sut.QueryCalendarResourceHrefsAsync(
                "https://example.com/calendars/user/events/",
                CalendarEntityKind.Event,
                DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T11:00:00Z"),
                CancellationToken.None));

        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task QueryCalendarResourceHrefsAsync_IgnoresSuccessfulCollectionSelfResponse()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent(
                "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/calendars/user/events/</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response><d:response><d:href>/calendars/user/events/a.ics</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response></d:multistatus>",
                Encoding.UTF8,
                "application/xml")
        });
        var sut = CreateSut(handler);

        var result = await sut.QueryCalendarResourceHrefsAsync(
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
    public async Task QueryCalendarResourceHrefsAsync_RejectsUnsafeReportCandidateBeforeAnyGet(string candidateHref)
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

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryCalendarResourceHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None));

        requests.ShouldHaveSingleItem().Method.Method.ShouldBe("REPORT");
    }

    [Fact]
    public async Task QueryCalendarResourceHrefsAsync_RejectsCrossOriginCalendarBeforeNetwork()
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.MultiStatus);
        }));

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryCalendarResourceHrefsAsync(
            "https://other.example/calendars/user/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None));

        requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryCalendarResourceHrefsAsync_RejectsSeeOtherWithoutFollowing()
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

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryCalendarResourceHrefsAsync(
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
    public async Task QueryCalendarResourceHrefsAsync_FollowsRedirectButRejectsCandidateOutsideAuthorizedCalendarIdentity(
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

        await Should.ThrowAsync<CalendarDiscoveryProtocolException>(() => sut.QueryCalendarResourceHrefsAsync(
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
    public async Task QueryCalendarResourceHrefsAsync_RejectsOversizedContentLength()
    {
        var content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]);
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = content
        }));

        var exception = await Should.ThrowAsync<HttpRequestException>(() => sut.QueryCalendarResourceHrefsAsync(
            "https://example.com/calendars/user/events/",
            CalendarEntityKind.Event,
            null,
            null,
            CancellationToken.None));

        exception.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task QueryCalendarResourceHrefsAsync_StopsUnknownLengthBodyAtLimitPlusOne()
    {
        var stream = new CountingNonSeekableStream(new byte[4 * 1024 * 1024 + 8192]);
        var sut = CreateSut(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.MultiStatus)
        {
            Content = new StreamContent(stream)
        }));

        var exception = await Should.ThrowAsync<HttpRequestException>(() => sut.QueryCalendarResourceHrefsAsync(
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
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { ETag = new EntityTagHeaderValue("\"revision-1\"") },
            Content = new ByteArrayContent(payload)
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

    #region CreateTaskAsync Tests

    [Fact]
    public async Task CreateTaskAsync_RelativeLocationHeader_ReturnsAbsoluteHref()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                Headers =
                {
                    Location = new Uri("/remote.php/dav/calendars/user/tasks/generated.ics", UriKind.Relative),
                    ETag = new EntityTagHeaderValue("\"etag-123\"")
                }
            };

            return response;
        });

        var sut = CreateSut(handler);
        var task = new TaskItem { Summary = "New task" };

        // Act
        var result = await sut.CreateTaskAsync("/remote.php/dav/calendars/user/tasks/", task, CancellationToken.None);

        // Assert
        result.Href.ShouldBe("https://example.com/remote.php/dav/calendars/user/tasks/generated.ics");
        result.ETag.ShouldBe("etag-123");
    }

    [Fact]
    public async Task CreateTaskAsync_UsesPutMethodAndIfNoneMatchHeader()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"etag-123\"") }
            };
        });

        var sut = CreateSut(handler);
        var task = new TaskItem { Summary = "New task", Uid = "test-uid" };

        // Act
        await sut.CreateTaskAsync("/calendars/user/tasks/", task, CancellationToken.None);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Method.Method.ShouldBe("PUT");
        capturedRequest.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues).ShouldBeTrue();
        ifNoneMatchValues!.ShouldContain("*");
    }

    [Fact]
    public async Task CreateTaskAsync_SendsTextCalendarContentType()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"etag-123\"") }
            };
        });

        var sut = CreateSut(handler);
        var task = new TaskItem { Summary = "New task", Uid = "test-uid" };

        // Act
        await sut.CreateTaskAsync("/calendars/user/tasks/", task, CancellationToken.None);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Content!.Headers.ContentType!.MediaType.ShouldBe("text/calendar");
    }

    [Fact]
    public async Task CreateTaskAsync_EscapesSpecialCharactersInUid_WhenUidContainsReservedChars()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"etag-123\"") }
            };
        });

        var sut = CreateSut(handler);
        var task = new TaskItem { Summary = "New task", Uid = "with spaces and #special" };

        await sut.CreateTaskAsync("/calendars/user/tasks/", task, CancellationToken.None);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.RequestUri.ShouldNotBeNull();
        capturedRequest.RequestUri.AbsoluteUri.ShouldContain("with%20spaces%20and%20%23special.ics");
    }

    #endregion

    #region GetTaskAsync Tests

    [Fact]
    public async Task GetTaskAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTaskAsync("/calendars/user/tasks/missing.ics", CancellationToken.None);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetTaskAsync_UsesGetMethod()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:test-uid\r\nSUMMARY:Test task\r\nEND:VTODO\r\nEND:VCALENDAR"),
                Headers = { ETag = new EntityTagHeaderValue("\"etag-456\"") }
            };
        });

        var sut = CreateSut(handler);

        // Act
        await sut.GetTaskAsync("/calendars/user/tasks/test.ics", CancellationToken.None);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Method.ShouldBe(HttpMethod.Get);
    }

    [Fact]
    public async Task GetTaskAsync_PopulatesETagFromResponse()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:test-uid\r\nSUMMARY:Test task\r\nEND:VTODO\r\nEND:VCALENDAR"),
                Headers = { ETag = new EntityTagHeaderValue("\"etag-789\"") }
            });

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTaskAsync("/calendars/user/tasks/test.ics", CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.ETag.ShouldBe("etag-789");
    }

    [Fact]
    public async Task GetTaskAsync_UsesAbsoluteHref_AsIs()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:test-uid\r\nSUMMARY:Test task\r\nEND:VTODO\r\nEND:VCALENDAR"),
                Headers = { ETag = new EntityTagHeaderValue("\"etag-789\"") }
            };
        });

        var sut = CreateSut(handler);

        // Act
        await sut.GetTaskAsync("https://example.com/remote.php/dav/calendars/user/tasks/test.ics", CancellationToken.None);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.RequestUri!.AbsoluteUri.ShouldBe("https://example.com/remote.php/dav/calendars/user/tasks/test.ics");
    }

    [Fact]
    public async Task GetTaskAsync_ReturnsNull_WhenCalendarDataHasNoVTODO()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:event-1\r\nSUMMARY:Meeting\r\nEND:VEVENT\r\nEND:VCALENDAR"),
                Headers = { ETag = new EntityTagHeaderValue("\"etag-789\"") }
            });

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTaskAsync("/calendars/user/tasks/test.ics", CancellationToken.None);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetTaskAsync_ResolvesRelativeHrefWithoutLeadingSlash()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:test-uid\r\nSUMMARY:Test task\r\nEND:VTODO\r\nEND:VCALENDAR"),
                Headers = { ETag = new EntityTagHeaderValue("\"etag-789\"") }
            };
        });

        var sut = CreateSut(handler);

        // Act
        await sut.GetTaskAsync("calendars/user/tasks/test.ics", CancellationToken.None);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.RequestUri!.AbsoluteUri.ShouldBe("https://example.com/remote.php/dav/calendars/user/tasks/test.ics");
    }

    #endregion

    #region UpdateTaskAsync Tests

    [Fact]
    public async Task UpdateTaskAsync_UsesPutMethod()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"etag-new\"") }
            };
        });

        var sut = CreateSut(handler);
        var task = new TaskItem { Uid = "test-uid", Href = "/calendars/user/tasks/test.ics", Summary = "Updated" };

        // Act
        await sut.UpdateTaskAsync(task, CancellationToken.None);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Method.Method.ShouldBe("PUT");
    }

    [Fact]
    public async Task UpdateTaskAsync_SendsIfMatchHeader_WhenETagPresent()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"etag-new\"") }
            };
        });

        var sut = CreateSut(handler);
        var task = new TaskItem
        {
            Uid = "test-uid",
            Href = "/calendars/user/tasks/test.ics",
            Summary = "Updated",
            ETag = "etag-old"
        };

        // Act
        await sut.UpdateTaskAsync(task, CancellationToken.None);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Headers.IfMatch.ShouldContain(new EntityTagHeaderValue("\"etag-old\""));
    }

    [Fact]
    public async Task UpdateTaskAsync_ThrowsCalDavConflictException_On412PreconditionFailed()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"current-etag\"") }
            });

        var sut = CreateSut(handler);
        var task = new TaskItem { Uid = "test-uid", Href = "/calendars/user/tasks/test.ics", Summary = "Updated" };

        // Act & Assert
        var ex = await Should.ThrowAsync<CalDavConflictException>(() =>
            sut.UpdateTaskAsync(task, CancellationToken.None));

        ex.Href.ShouldBe("/calendars/user/tasks/test.ics");
        ex.CurrentEtag.ShouldBe("current-etag");
    }

    [Fact]
    public async Task UpdateTaskAsync_ThrowsCalDavConflictException_WithNullETag_When412HasNoETag()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.PreconditionFailed));

        var sut = CreateSut(handler);
        var task = new TaskItem { Uid = "test-uid", Href = "/calendars/user/tasks/test.ics", Summary = "Updated" };

        // Act & Assert
        var ex = await Should.ThrowAsync<CalDavConflictException>(() =>
            sut.UpdateTaskAsync(task, CancellationToken.None));

        ex.Href.ShouldBe("/calendars/user/tasks/test.ics");
        ex.CurrentEtag.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateTaskAsync_ReturnsUpdatedETagFromResponse()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"new-etag\"") }
            });

        var sut = CreateSut(handler);
        var task = new TaskItem { Uid = "test-uid", Href = "/calendars/user/tasks/test.ics", Summary = "Updated" };

        // Act
        var result = await sut.UpdateTaskAsync(task, CancellationToken.None);

        // Assert
        result.ETag.ShouldBe("new-etag");
    }

    [Fact]
    public async Task UpdateTaskAsync_PreservesExistingETag_WhenResponseHasNone()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));

        var sut = CreateSut(handler);
        var task = new TaskItem
        {
            Uid = "test-uid",
            Href = "/calendars/user/tasks/test.ics",
            Summary = "Updated",
            ETag = "existing-etag"
        };

        // Act
        var result = await sut.UpdateTaskAsync(task, CancellationToken.None);

        // Assert
        result.ETag.ShouldBe("existing-etag");
    }

    [Fact]
    public async Task UpdateTaskAsync_UsesExistingETag_WhenResponseHasNoETag()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));

        var sut = CreateSut(handler);
        var task = new TaskItem
        {
            Uid = "test-uid",
            Href = "/calendars/user/tasks/test.ics",
            Summary = "Updated",
            ETag = "existing-etag"
        };

        // Act
        var result = await sut.UpdateTaskAsync(task, CancellationToken.None);

        // Assert
        result.ETag.ShouldBe("existing-etag");
    }

    #endregion

    #region DeleteTaskAsync Tests

    [Fact]
    public async Task DeleteTaskAsync_UsesDeleteMethod()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var sut = CreateSut(handler);

        // Act
        await sut.DeleteTaskAsync("/calendars/user/tasks/test.ics", null, CancellationToken.None);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Method.ShouldBe(HttpMethod.Delete);
    }

    [Fact]
    public async Task DeleteTaskAsync_SendsIfMatchHeader_WhenETagPresent()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var sut = CreateSut(handler);

        // Act
        await sut.DeleteTaskAsync("/calendars/user/tasks/test.ics", "etag-to-delete", CancellationToken.None);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Headers.IfMatch.ShouldContain(new EntityTagHeaderValue("\"etag-to-delete\""));
    }

    [Fact]
    public async Task DeleteTaskAsync_ThrowsCalDavConflictException_On412PreconditionFailed()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"current-etag\"") }
            });

        var sut = CreateSut(handler);

        // Act & Assert
        var ex = await Should.ThrowAsync<CalDavConflictException>(() =>
            sut.DeleteTaskAsync("/calendars/user/tasks/test.ics", "stale-etag", CancellationToken.None));

        ex.Href.ShouldBe("/calendars/user/tasks/test.ics");
        ex.CurrentEtag.ShouldBe("current-etag");
    }

    [Fact]
    public async Task DeleteTaskAsync_Succeeds_WhenNotFound()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var sut = CreateSut(handler);

        // Act & Assert - should not throw
        await sut.DeleteTaskAsync("/calendars/user/tasks/missing.ics", null, CancellationToken.None);
    }

    [Fact]
    public async Task DeleteTaskAsync_ThrowsCalDavConflictException_WithNullETag_When412HasNoETag()
    {
        // Arrange
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.PreconditionFailed));

        var sut = CreateSut(handler);

        // Act & Assert
        var ex = await Should.ThrowAsync<CalDavConflictException>(() =>
            sut.DeleteTaskAsync("/calendars/user/tasks/test.ics", "stale-etag", CancellationToken.None));

        ex.Href.ShouldBe("/calendars/user/tasks/test.ics");
        ex.CurrentEtag.ShouldBeNull();
    }

    #endregion

    #region PROPFIND and REPORT Tests

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
        var sut = CreateSut(handler, "https://example.com/remote.php/dav/");

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
        var sut = CreateSut(handler, "https://example.com/remote.php/dav/");

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
        var sut = CreateSut(handler, "https://example.com/remote.php/dav/");

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
        var sut = CreateSut(handler, "https://example.com/remote.php/dav/");

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

    [Fact]
    public async Task GetTaskListsAsync_SendsPropFindWithDepthHeader()
    {
        // Arrange
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
            new List<HttpResponseMessage>
            {
                new(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\">" +
                            "<d:response>" +
                                "<d:href>/calendars/user/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<cal:calendar-home-set xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                                            "<d:href>/calendars/user/</d:href>" +
                                        "</cal:calendar-home-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                },
                new(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                            "<d:response>" +
                                "<d:href>/calendars/user/tasks/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<d:resourcetype>" +
                                            "<cal:calendar/>" +
                                        "</d:resourcetype>" +
                                        "<d:displayname>Tasks</d:displayname>" +
                                        "<cal:supported-calendar-component-set>" +
                                            "<cal:comp name=\"VTODO\"/>" +
                                        "</cal:supported-calendar-component-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                }
            },
            requests);

        var sut = CreateSut(handler, "https://example.com/");

        // Act
        await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requests.Count.ShouldBe(2);
        // First PROPFIND is the calendar-home-set discovery (Depth: 0)
        requests[0].Method.Method.ShouldBe("PROPFIND");
        requests[0].Headers.TryGetValues("Depth", out var depth0).ShouldBeTrue();
        depth0!.First().ShouldBe("0");
        // Second PROPFIND is the calendar list retrieval (Depth: 1)
        requests[1].Method.Method.ShouldBe("PROPFIND");
        requests[1].Headers.TryGetValues("Depth", out var depthValue).ShouldBeTrue();
        depthValue!.First().ShouldBe("1");
    }

    [Fact]
    public async Task GetTaskListsAsync_SendsApplicationXmlContentType()
    {
        // Arrange — must provide distinct responses for the two PROPFIND calls
        var requests = new List<HttpRequestMessage>();
        var handler = CreateSequencedHandler(
            new List<HttpResponseMessage>
            {
                new(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\">" +
                            "<d:response>" +
                                "<d:href>/calendars/user/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<cal:calendar-home-set xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                                            "<d:href>/calendars/user/</d:href>" +
                                        "</cal:calendar-home-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                },
                new(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                            "<d:response>" +
                                "<d:href>/calendars/user/tasks/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<d:resourcetype>" +
                                            "<cal:calendar/>" +
                                        "</d:resourcetype>" +
                                        "<d:displayname>Tasks</d:displayname>" +
                                        "<cal:supported-calendar-component-set>" +
                                            "<cal:comp name=\"VTODO\"/>" +
                                        "</cal:supported-calendar-component-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                }
            },
            requests);

        var sut = CreateSut(handler, "https://example.com/");

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert — both PROPFIND requests should send application/xml content type
        requests.Count.ShouldBe(2);
        requests.All(r => r.Method.Method == "PROPFIND").ShouldBeTrue();
        requests.All(r => r.Content!.Headers.ContentType!.MediaType == "application/xml").ShouldBeTrue();

        // Verify the two-request flow produced a valid result — this ensures the test
        // passes because the production code correctly processed distinct responses,
        // not just because content-type happens to be application/xml on both requests.
        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetTaskListsAsync_CalendarHomeSetDiscovery_SendsDepth0AndApplicationXmlContentType()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateCalendarHomeSetDiscoveryDepth0Handler(requests);

        var sut = CreateSut(handler, "https://example.com/");

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert — the initial PROPFIND for calendar-home-set discovery
        requests.Count.ShouldBe(2);
        var discoveryRequest = requests[0];
        discoveryRequest.Method.Method.ShouldBe("PROPFIND");
        discoveryRequest.Headers.TryGetValues("Depth", out var depth).ShouldBeTrue();
        depth!.First().ShouldBe("0");
        discoveryRequest.Content!.Headers.ContentType!.MediaType.ShouldBe("application/xml");

        // Verify the two-request flow produced a valid result — this ensures the test
        // passes because the production code correctly processed distinct responses,
        // not just because depth and content-type happen to be correct on request 0.
        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetTasksAsync_SendsReportWithDepthHeader()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                    "<d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                    "</d:multistatus>", Encoding.UTF8, "application/xml")
            };
        });

        var sut = CreateSut(handler);
        var query = new TaskQuery();

        // Act
        await sut.GetTasksAsync("/calendars/user/tasks/", query, CancellationToken.None);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Method.Method.ShouldBe("REPORT");
        capturedRequest.Headers.TryGetValues("Depth", out var depthValue).ShouldBeTrue();
        depthValue!.First().ShouldBe("1");
    }

    [Fact]
    public async Task GetTasksAsync_SendsApplicationXmlContentType()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                    "<d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                    "</d:multistatus>", Encoding.UTF8, "application/xml")
            };
        });

        var sut = CreateSut(handler);
        var query = new TaskQuery();

        // Act
        await sut.GetTasksAsync("/calendars/user/tasks/", query, CancellationToken.None);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Content!.Headers.ContentType!.MediaType.ShouldBe("application/xml");
    }

    [Fact]
    public async Task GetTasksAsync_SendsReportWithCompletedStatusFilter_WhenStatusIsCompleted()
    {
        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"></d:multistatus>",
                    Encoding.UTF8,
                    "application/xml")
            };
        });

        var sut = CreateSut(handler);
        var query = new TaskQuery { Status = DotnetAgents.CalDav.Core.Models.TaskStatus.Completed };

        // Act
        var result = await sut.GetTasksAsync("calendars/user/tasks/", query, TestContext.Current.CancellationToken);

        // Assert
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Method.Method.ShouldBe("REPORT");
        var requestBody = await capturedRequest.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        requestBody.ShouldContain("COMPLETED");
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTasksAsync_WithNeedsActionStatus_DoesNotUseIsNotDefinedFilter()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new AsyncStubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"></d:multistatus>",
                    Encoding.UTF8,
                    "application/xml")
            });
        });

        var sut = CreateSut(handler);

        await sut.GetTasksAsync("/calendars/user/tasks/", new TaskQuery { Status = DotnetAgents.CalDav.Core.Models.TaskStatus.NeedsAction }, CancellationToken.None);

        capturedRequest.ShouldNotBeNull();
        var requestBody = await capturedRequest.Content!.ReadAsStringAsync(CancellationToken.None);
        requestBody.ShouldNotContain("is-not-defined");
    }

    [Fact]
    public async Task GetTasksAsync_WithInProcessStatus_DoesNotUseIsNotDefinedFilter()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new AsyncStubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"></d:multistatus>",
                    Encoding.UTF8,
                    "application/xml")
            });
        });

        var sut = CreateSut(handler);

        await sut.GetTasksAsync("/calendars/user/tasks/", new TaskQuery { Status = DotnetAgents.CalDav.Core.Models.TaskStatus.InProcess }, CancellationToken.None);

        capturedRequest.ShouldNotBeNull();
        var requestBody = await capturedRequest.Content!.ReadAsStringAsync(CancellationToken.None);
        requestBody.ShouldNotContain("is-not-defined");
    }

    [Fact]
    public async Task GetTasksAsync_SkipsMalformedICalData_WhenFromICalTextThrows()
    {
        // Arrange
        var xml = BuildTasksResponseXml(
            ("/calendars/user/tasks/task1.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:test-uid\r\nSUMMARY:Test task\r\nEND:VCALENDAR"),
            ("/calendars/user/tasks/task2.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task2\r\nSUMMARY:Valid task\r\nEND:VTODO\r\nEND:VCALENDAR")
        );

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTasksAsync("/calendars/user/tasks/", new TaskQuery(), CancellationToken.None);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Uid.ShouldBe("task2");
    }

    [Fact]
    public async Task GetTasksAsync_FollowsRedirect_WhenServerReturns308()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = CreateGetTasksRedirectHandler(requests);

        var sut = CreateSut(handler);

        await sut.GetTasksAsync("/calendars/user/tasks/", new TaskQuery(), CancellationToken.None);

        requests.Count.ShouldBe(2);
        requests[1].Method.Method.ShouldBe("REPORT");
        var redirectedUri = requests[1].RequestUri;
        redirectedUri.ShouldNotBeNull();
        redirectedUri.AbsoluteUri.ShouldBe("https://example.com/new-path/");
    }

    #endregion

    #region Discovery Fallback Tests

    [Fact]
    public async Task GetTaskListsAsync_FallsBackToBaseUrl_WhenWellKnownFails()
    {
        // Arrange
        var requests = new List<HttpRequestMessage>();
        var handler = CreateWellKnownFailureFallbackHandler(requests);

        var sut = CreateSut(handler, "https://example.com/dav/");

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requests.Count.ShouldBe(3);
        requests[0].RequestUri!.PathAndQuery.ShouldBe("/.well-known/caldav");
        requests[1].RequestUri!.PathAndQuery.ShouldBe("/dav/");
        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetTaskListsAsync_ReturnsEmptyList_WhenHomeSetDiscoveryReturnsNull()
    {
        // Arrange
        var requestCount = 0;
        var handler = new AsyncStubHttpMessageHandler(_ =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"></d:multistatus>",
                    Encoding.UTF8,
                    "application/xml")
            });
        });

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requestCount.ShouldBe(2);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTaskListsAsync_ReturnsNull_WhenBaseUrlDiscoveryThrows()
    {
        // Arrange
        var requests = new List<HttpRequestMessage>();
        var handler = CreateBaseUrlDiscoveryThrowsHandler(requests);

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requests.Count.ShouldBe(2);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTaskListsAsync_FallsBackToBaseUrl_WhenWellKnownReturnsNotFound()
    {
        // Arrange
        var requests = new List<HttpRequestMessage>();
        var handler = CreateWellKnownNotFoundFallbackHandler(requests);

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requests.Count.ShouldBe(3);
        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetTaskListsAsync_ReturnsEmptyList_WhenCurrentUserPrincipalIsMissing()
    {
        // Arrange
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:propstat><d:prop /></d:propstat></d:response></d:multistatus>",
                    Encoding.UTF8,
                    "application/xml")
            };
        });

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requestCount.ShouldBe(2);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTaskListsAsync_ReturnsEmptyList_WhenCalendarHomeSetNotFound()
    {
        // Arrange
        var requests = new List<HttpRequestMessage>();
        var handler = CreateCalendarHomeSetNotFoundHandler(requests);

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requests.Count.ShouldBe(2);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTasksAsync_CreatesActivity_WhenListenerRegistered()
    {
        // Arrange
        var started = false;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "DotnetAgents.CalDav",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => started = true
        };

        ActivitySource.AddActivityListener(listener);

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"></d:multistatus>",
                    Encoding.UTF8,
                    "application/xml")
            });

        var sut = CreateSut(handler);

        // Act
        await sut.GetTasksAsync("/calendars/user/tasks/", new TaskQuery(), CancellationToken.None);

        // Assert
        started.ShouldBeTrue();
    }

    [Fact]
    public async Task GetTaskAsync_CreatesActivity_WhenListenerRegistered()
    {
        // Arrange
        var started = false;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "DotnetAgents.CalDav",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => started = true
        };

        ActivitySource.AddActivityListener(listener);

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:test-uid\r\nSUMMARY:Test task\r\nEND:VTODO\r\nEND:VCALENDAR"),
                Headers = { ETag = new EntityTagHeaderValue("\"etag-789\"") }
            });

        var sut = CreateSut(handler);

        // Act
        await sut.GetTaskAsync("/calendars/user/tasks/test.ics", CancellationToken.None);

        // Assert
        started.ShouldBeTrue();
    }

    [Fact]
    public async Task GetTaskListsAsync_FollowsRedirect_WhenWellKnownReturnsRedirect()
    {
        // Arrange
        var requests = new List<HttpRequestMessage>();
        var handler = CreateWellKnownRedirectHandler(requests);

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requests.Count.ShouldBe(3);
        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetTaskAsync_Throws_WhenCalendarDataIsMalformed()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-valid-ical")
            });

        var sut = CreateSut(handler);

        await Should.ThrowAsync<Exception>(() =>
            sut.GetTaskAsync("/calendars/user/tasks/test.ics", CancellationToken.None));
    }

    #endregion

    #region Client-side Query Filtering Tests

    [Fact]
    public async Task GetTasksAsync_FiltersByTextSearchInSummary()
    {
        // Arrange — "grocery" is a substring of "Buy grocery items"
        var xml = BuildTasksResponseXml(
            ("/calendars/user/tasks/task1.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task1\r\nSUMMARY:Buy grocery items\r\nEND:VTODO\r\nEND:VCALENDAR"),
            ("/calendars/user/tasks/task2.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task2\r\nSUMMARY:Call dentist\r\nEND:VTODO\r\nEND:VCALENDAR")
        );

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });

        var sut = CreateSut(handler);
        var query = new TaskQuery { TextSearch = "grocery" };

        // Act
        var result = await sut.GetTasksAsync("/calendars/user/tasks/", query, CancellationToken.None);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Summary.ShouldBe("Buy grocery items");
    }

    [Fact]
    public async Task GetTasksAsync_FiltersByTextSearchInDescription()
    {
        // Arrange
        var xml = BuildTasksResponseXml(
            ("/calendars/user/tasks/task1.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task1\r\nSUMMARY:Task One\r\nDESCRIPTION:Buy organic milk and eggs\r\nEND:VTODO\r\nEND:VCALENDAR"),
            ("/calendars/user/tasks/task2.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task2\r\nSUMMARY:Task Two\r\nDESCRIPTION:Review quarterly report\r\nEND:VTODO\r\nEND:VCALENDAR")
        );

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });

        var sut = CreateSut(handler);
        var query = new TaskQuery { TextSearch = "milk" };

        // Act
        var result = await sut.GetTasksAsync("/calendars/user/tasks/", query, CancellationToken.None);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Description.ShouldBe("Buy organic milk and eggs");
    }

    [Fact]
    public async Task GetTasksAsync_FiltersByCategory()
    {
        // Arrange
        var xml = BuildTasksResponseXml(
            ("/calendars/user/tasks/task1.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task1\r\nSUMMARY:Work Task\r\nCATEGORIES:work\r\nEND:VTODO\r\nEND:VCALENDAR"),
            ("/calendars/user/tasks/task2.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task2\r\nSUMMARY:Personal Task\r\nCATEGORIES:personal\r\nEND:VTODO\r\nEND:VCALENDAR")
        );

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });

        var sut = CreateSut(handler);
        var query = new TaskQuery { Category = "work" };

        // Act
        var result = await sut.GetTasksAsync("/calendars/user/tasks/", query, CancellationToken.None);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Summary.ShouldBe("Work Task");
    }

    [Fact]
    public async Task GetTasksAsync_FiltersByDueAfterDate()
    {
        // Arrange — Past Task due 2024-01-01, Future Task due 2024-12-31
        var xml = BuildTasksResponseXml(
            ("/calendars/user/tasks/task1.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task1\r\nSUMMARY:Past Task\r\nDUE:20240101T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR"),
            ("/calendars/user/tasks/task2.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task2\r\nSUMMARY:Future Task\r\nDUE:20241231T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR")
        );

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });

        var sut = CreateSut(handler);
        var query = new TaskQuery { DueAfter = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero) };

        // Act
        var result = await sut.GetTasksAsync("/calendars/user/tasks/", query, CancellationToken.None);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Summary.ShouldBe("Future Task");
    }

    [Fact]
    public async Task GetTasksAsync_FiltersByDueBeforeDate()
    {
        // Arrange — Past Task due 2024-01-01, Future Task due 2024-12-31
        var xml = BuildTasksResponseXml(
            ("/calendars/user/tasks/task1.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task1\r\nSUMMARY:Past Task\r\nDUE:20240101T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR"),
            ("/calendars/user/tasks/task2.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task2\r\nSUMMARY:Future Task\r\nDUE:20241231T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR")
        );

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });

        var sut = CreateSut(handler);
        var query = new TaskQuery { DueBefore = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero) };

        // Act
        var result = await sut.GetTasksAsync("/calendars/user/tasks/", query, CancellationToken.None);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Summary.ShouldBe("Past Task");
    }

    [Fact]
    public async Task GetTasksAsync_ExcludesTasksWithoutDueDate_WhenDateFilterApplied()
    {
        // Arrange — one task with a due date, one without
        var xml = BuildTasksResponseXml(
            ("/calendars/user/tasks/task1.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task1\r\nSUMMARY:Task With Due\r\nDUE:20241231T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR"),
            ("/calendars/user/tasks/task2.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task2\r\nSUMMARY:Task Without Due\r\nEND:VTODO\r\nEND:VCALENDAR")
        );

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });

        var sut = CreateSut(handler);
        var query = new TaskQuery { DueAfter = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero) };

        // Act
        var result = await sut.GetTasksAsync("/calendars/user/tasks/", query, CancellationToken.None);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Summary.ShouldBe("Task With Due");
    }

    [Fact]
    public async Task GetTasksAsync_ReturnsMatchingTask_WhenQueryMatchesAllFilters()
    {
        // Arrange
        var xml = BuildTasksResponseXml(
            ("/calendars/user/tasks/task1.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task1\r\nSUMMARY:Pay milk bill\r\nDESCRIPTION:Remember to pay milk bill\r\nDUE:20240615T120000Z\r\nCATEGORIES:work\r\nSTATUS:NEEDS-ACTION\r\nEND:VTODO\r\nEND:VCALENDAR"),
            ("/calendars/user/tasks/task2.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:task2\r\nSUMMARY:Other task\r\nDUE:20240615T120000Z\r\nCATEGORIES:personal\r\nSTATUS:NEEDS-ACTION\r\nEND:VTODO\r\nEND:VCALENDAR")
        );

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });

        var sut = CreateSut(handler);
        var query = new TaskQuery
        {
            Status = DotnetAgents.CalDav.Core.Models.TaskStatus.NeedsAction,
            DueAfter = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
            DueBefore = new DateTimeOffset(2024, 6, 30, 0, 0, 0, TimeSpan.Zero),
            TextSearch = "milk",
            Category = "work"
        };

        // Act
        var result = await sut.GetTasksAsync("/calendars/user/tasks/", query, CancellationToken.None);

        // Assert
        result.Count.ShouldBe(1);
        result[0].Uid.ShouldBe("task1");
    }

    #endregion

    #region TaskLists Configuration Filtering Tests

    [Fact]
    public async Task GetTaskListsAsync_AppliesConfiguredTaskListsFilter()
    {
        // Arrange
        var handler = CreateConfiguredTaskListsFilterHandler();

        var options = new CalDavOptions { BaseUrl = "https://example.com/", TaskLists = "work" };
        var sut = new CalDavClient(new HttpClient(handler), Options.Create(options), Substitute.For<ILogger<CalDavClient>>());

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        result.Count.ShouldBe(1);
        result[0].DisplayName.ShouldBe("Work");
    }

    [Fact]
    public async Task GetTaskListsAsync_AppliesMultipleTaskListsFilter()
    {
        // Arrange
        var handler = CreateMultipleTaskListsFilterHandler();

        var options = new CalDavOptions { BaseUrl = "https://example.com/", TaskLists = "work, personal" };
        var sut = new CalDavClient(new HttpClient(handler), Options.Create(options), Substitute.For<ILogger<CalDavClient>>());

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        result.Count.ShouldBe(2);
        result.Select(tl => tl.DisplayName).ShouldContain("Work");
        result.Select(tl => tl.DisplayName).ShouldContain("Personal");
    }

    #endregion

    #region Helper Methods

    private static CalDavClient CreateSut(HttpMessageHandler handler, string baseUrl = "https://example.com/remote.php/dav")
    {
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = baseUrl,
        });

        return new CalDavClient(httpClient, options, Substitute.For<ILogger<CalDavClient>>());
    }

    private static CalendarResourceCreateRequest CreateCalendarResourceRequest(string name) => new(
        "https://example.com/calendars/user/events/",
        "https://example.com/calendars/user/events/" + name,
        Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"));

    private static string BuildTasksResponseXml(params (string Href, string ICalData)[] tasks)
    {
        var Dav = System.Xml.Linq.XNamespace.Get("DAV:");
        var CalDav = System.Xml.Linq.XNamespace.Get("urn:ietf:params:xml:ns:caldav");

        var multistatus = new System.Xml.Linq.XElement(Dav + "multistatus");
        var doc = new System.Xml.Linq.XDocument(new System.Xml.Linq.XDeclaration("1.0", "utf-8", null), multistatus);

        foreach (var (href, icalData) in tasks)
        {
            multistatus.Add(new System.Xml.Linq.XElement(Dav + "response",
                new System.Xml.Linq.XElement(Dav + "href", href),
                new System.Xml.Linq.XElement(Dav + "propstat",
                    new System.Xml.Linq.XElement(Dav + "prop",
                        new System.Xml.Linq.XElement(CalDav + "calendar-data", icalData)),
                    new System.Xml.Linq.XElement(Dav + "status", "HTTP/1.1 200 OK"))));
        }

        return doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    private static StubHttpMessageHandler CreateCalendarHomeSetDiscoveryDepth0Handler(List<HttpRequestMessage> requests)
    {
        return CreateSequencedHandler(
            [
                new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\">" +
                            "<d:response>" +
                                "<d:href>/calendars/user/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<cal:calendar-home-set xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                                            "<d:href>/calendars/user/</d:href>" +
                                        "</cal:calendar-home-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                },
                new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                            "<d:response>" +
                                "<d:href>/calendars/user/tasks/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<d:resourcetype>" +
                                            "<cal:calendar/>" +
                                        "</d:resourcetype>" +
                                        "<d:displayname>Tasks</d:displayname>" +
                                        "<cal:supported-calendar-component-set>" +
                                            "<cal:comp name=\"VTODO\"/>" +
                                        "</cal:supported-calendar-component-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                }
            ],
            requests);
    }

    private static AsyncStubHttpMessageHandler CreateGetTasksRedirectHandler(List<HttpRequestMessage> requests)
    {
        return CreateAsyncSequencedHandler(
            [
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
                {
                    Headers = { Location = new Uri("/new-path/", UriKind.Relative) }
                }),
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                })
            ],
            requests);
    }

    private static AsyncStubHttpMessageHandler CreateWellKnownFailureFallbackHandler(List<HttpRequestMessage> requests)
    {
        return CreateAsyncSequencedHandler(
            [
                () => Task.FromException<HttpResponseMessage>(new HttpRequestException("Not found")),
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\">" +
                            "<d:response>" +
                                "<d:href>/dav/calendars/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<cal:calendar-home-set xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                                            "<d:href>/dav/calendars/user/</d:href>" +
                                        "</cal:calendar-home-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                }),
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                            "<d:response>" +
                                "<d:href>/dav/calendars/user/tasks/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<d:resourcetype>" +
                                            "<cal:calendar/>" +
                                        "</d:resourcetype>" +
                                        "<d:displayname>Tasks</d:displayname>" +
                                        "<cal:supported-calendar-component-set>" +
                                            "<cal:comp name=\"VTODO\"/>" +
                                        "</cal:supported-calendar-component-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                })
            ],
            requests);
    }

    private static AsyncStubHttpMessageHandler CreateBaseUrlDiscoveryThrowsHandler(List<HttpRequestMessage> requests)
    {
        return CreateAsyncSequencedHandler(
            [
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"><d:response /></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                }),
                () => Task.FromException<HttpResponseMessage>(new HttpRequestException("Base URL unavailable"))
            ],
            requests);
    }

    private static StubHttpMessageHandler CreateWellKnownNotFoundFallbackHandler(List<HttpRequestMessage> requests)
    {
        return CreateSequencedHandler(
            [
                new HttpResponseMessage(HttpStatusCode.NotFound),
                new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:propstat><d:prop><cal:calendar-home-set xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><d:href>/calendars/user/</d:href></cal:calendar-home-set></d:prop></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                },
                new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><d:response><d:href>/calendars/user/tasks/</d:href><d:propstat><d:prop><d:resourcetype><cal:calendar/></d:resourcetype><d:displayname>Tasks</d:displayname><cal:supported-calendar-component-set><cal:comp name=\"VTODO\"/></cal:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                }
            ],
            requests);
    }

    private static StubHttpMessageHandler CreateCalendarHomeSetNotFoundHandler(List<HttpRequestMessage> requests)
    {
        return CreateSequencedHandler(
            [
                new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:propstat><d:prop><cal:current-user-principal xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><d:href>/principals/users/user/</d:href></cal:current-user-principal></d:prop></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                },
                new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:propstat><d:prop /></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                }
            ],
            requests);
    }

    private static AsyncStubHttpMessageHandler CreateWellKnownRedirectHandler(List<HttpRequestMessage> requests)
    {
        return CreateAsyncSequencedHandler(
            [
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently)
                {
                    Headers = { Location = new Uri("/redirected/caldav", UriKind.Relative) }
                }),
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:propstat><d:prop><cal:calendar-home-set xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><d:href>/calendars/user/</d:href></cal:calendar-home-set></d:prop></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                }),
                () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><d:response><d:href>/calendars/user/tasks/</d:href><d:propstat><d:prop><d:resourcetype><cal:calendar/></d:resourcetype><d:displayname>Tasks</d:displayname><cal:supported-calendar-component-set><cal:comp name=\"VTODO\"/></cal:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                })
            ],
            requests);
    }

    private static StubHttpMessageHandler CreateConfiguredTaskListsFilterHandler()
    {
        return CreateSequencedHandler(
            [
                new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\">" +
                            "<d:response>" +
                                "<d:href>/calendars/user/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<cal:calendar-home-set xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                                            "<d:href>/calendars/user/</d:href>" +
                                        "</cal:calendar-home-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                },
                new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                            "<d:response>" +
                                "<d:href>/calendars/user/work/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<d:resourcetype>" +
                                            "<cal:calendar/>" +
                                        "</d:resourcetype>" +
                                        "<d:displayname>Work</d:displayname>" +
                                        "<cal:supported-calendar-component-set>" +
                                            "<cal:comp name=\"VTODO\"/>" +
                                        "</cal:supported-calendar-component-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                            "<d:response>" +
                                "<d:href>/calendars/user/personal/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<d:resourcetype>" +
                                            "<cal:calendar/>" +
                                        "</d:resourcetype>" +
                                        "<d:displayname>Personal</d:displayname>" +
                                        "<cal:supported-calendar-component-set>" +
                                            "<cal:comp name=\"VTODO\"/>" +
                                        "</cal:supported-calendar-component-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                }
            ],
            []);
    }

    private static StubHttpMessageHandler CreateMultipleTaskListsFilterHandler()
    {
        return CreateSequencedHandler(
            [
                new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\">" +
                            "<d:response>" +
                                "<d:href>/calendars/user/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<cal:calendar-home-set xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                                            "<d:href>/calendars/user/</d:href>" +
                                        "</cal:calendar-home-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                },
                new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                        "<d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\">" +
                            "<d:response>" +
                                "<d:href>/calendars/user/work/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<d:resourcetype>" +
                                            "<cal:calendar/>" +
                                        "</d:resourcetype>" +
                                        "<d:displayname>Work</d:displayname>" +
                                        "<cal:supported-calendar-component-set>" +
                                            "<cal:comp name=\"VTODO\"/>" +
                                        "</cal:supported-calendar-component-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                            "<d:response>" +
                                "<d:href>/calendars/user/personal/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<d:resourcetype>" +
                                            "<cal:calendar/>" +
                                        "</d:resourcetype>" +
                                        "<d:displayname>Personal</d:displayname>" +
                                        "<cal:supported-calendar-component-set>" +
                                            "<cal:comp name=\"VTODO\"/>" +
                                        "</cal:supported-calendar-component-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                            "<d:response>" +
                                "<d:href>/calendars/user/shared/</d:href>" +
                                "<d:propstat>" +
                                    "<d:prop>" +
                                        "<d:resourcetype>" +
                                            "<cal:calendar/>" +
                                        "</d:resourcetype>" +
                                        "<d:displayname>Shared</d:displayname>" +
                                        "<cal:supported-calendar-component-set>" +
                                            "<cal:comp name=\"VTODO\"/>" +
                                        "</cal:supported-calendar-component-set>" +
                                    "</d:prop>" +
                                    "<d:status>HTTP/1.1 200 OK</d:status>" +
                                "</d:propstat>" +
                            "</d:response>" +
                        "</d:multistatus>", Encoding.UTF8, "application/xml")
                }
            ],
            []);
    }

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

    private static HttpResponseMessage CreateCalendarHomeSetResponse() =>
        new(HttpStatusCode.MultiStatus)
        {
            Content = new StringContent("""
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response><d:href>/calendars/user/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>/calendars/user/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
                </d:multistatus>
                """, Encoding.UTF8, "application/xml")
        };

    #endregion
}
