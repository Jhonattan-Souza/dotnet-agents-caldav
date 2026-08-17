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
