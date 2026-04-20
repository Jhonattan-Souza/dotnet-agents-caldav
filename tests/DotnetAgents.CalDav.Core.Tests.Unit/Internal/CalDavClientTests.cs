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
    public async Task GetTaskListsAsync_SendsPropFindWithDepthHeader()
    {
        // Arrange
        var requestCount = 0;
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            requests.Add(request);
            return requestCount switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                2 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        });

        var sut = CreateSut(handler, "https://example.com/");

        // Act
        await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requestCount.ShouldBe(2);
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
        var requestCount = 0;
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            requests.Add(request);
            return requestCount switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                2 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        });

        var sut = CreateSut(handler, "https://example.com/");

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert — both PROPFIND requests should send application/xml content type
        requestCount.ShouldBe(2);
        foreach (var req in requests)
        {
            req.Method.Method.ShouldBe("PROPFIND");
            req.Content!.Headers.ContentType!.MediaType.ShouldBe("application/xml");
        }

        // Verify the two-request flow produced a valid result — this ensures the test
        // passes because the production code correctly processed distinct responses,
        // not just because content-type happens to be application/xml on both requests.
        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetTaskListsAsync_CalendarHomeSetDiscovery_SendsDepth0AndApplicationXmlContentType()
    {
        // Arrange — capture both requests and return distinct responses for each PROPFIND
        var requestCount = 0;
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            requests.Add(request);
            return requestCount switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                2 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        });

        var sut = CreateSut(handler, "https://example.com/");

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert — the initial PROPFIND for calendar-home-set discovery
        requestCount.ShouldBe(2);
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
        var handler = new AsyncStubHttpMessageHandler(request =>
        {
            requests.Add(request);

            return Task.FromResult(requests.Count switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.PermanentRedirect)
                {
                    Headers = { Location = new Uri("/new-path/", UriKind.Relative) }
                },
                2 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                },
                _ => throw new InvalidOperationException("Unexpected request")
            });
        });

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
        var requestCount = 0;
        var requests = new List<HttpRequestMessage>();
        var handler = new AsyncStubHttpMessageHandler(async request =>
        {
            requestCount++;
            requests.Add(request);
            return requestCount switch
            {
                // First request to well-known fails as a faulted task (models true async failure)
                1 => await Task.FromException<HttpResponseMessage>(new HttpRequestException("Not found")),
                // Second request to base URL succeeds
                2 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                },
                // Third request to get calendars from home set
                3 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        });

        var sut = CreateSut(handler, "https://example.com/dav/");

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requestCount.ShouldBe(3);
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
        var requestCount = 0;
        var handler = new AsyncStubHttpMessageHandler(request =>
        {
            requestCount++;
            return requestCount switch
            {
                1 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"><d:response /></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                }),
                2 => Task.FromException<HttpResponseMessage>(new HttpRequestException("Base URL unavailable")),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
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
    public async Task GetTaskListsAsync_FallsBackToBaseUrl_WhenWellKnownReturnsNotFound()
    {
        // Arrange
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            return requestCount switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.NotFound),
                2 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:propstat><d:prop><cal:calendar-home-set xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><d:href>/calendars/user/</d:href></cal:calendar-home-set></d:prop></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                },
                3 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><d:response><d:href>/calendars/user/tasks/</d:href><d:propstat><d:prop><d:resourcetype><cal:calendar/></d:resourcetype><d:displayname>Tasks</d:displayname><cal:supported-calendar-component-set><cal:comp name=\"VTODO\"/></cal:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        });

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requestCount.ShouldBe(3);
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
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            return requestCount switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:propstat><d:prop><cal:current-user-principal xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><d:href>/principals/users/user/</d:href></cal:current-user-principal></d:prop></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                },
                2 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:propstat><d:prop /></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
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
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            return requestCount switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.MovedPermanently)
                {
                    Headers = { Location = new Uri("/redirected/caldav", UriKind.Relative) }
                },
                2 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:propstat><d:prop><cal:calendar-home-set xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><d:href>/calendars/user/</d:href></cal:calendar-home-set></d:prop></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                },
                3 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
                {
                    Content = new StringContent(
                        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><d:response><d:href>/calendars/user/tasks/</d:href><d:propstat><d:prop><d:resourcetype><cal:calendar/></d:resourcetype><d:displayname>Tasks</d:displayname><cal:supported-calendar-component-set><cal:comp name=\"VTODO\"/></cal:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>",
                        Encoding.UTF8,
                        "application/xml")
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        });

        var sut = CreateSut(handler);

        // Act
        var result = await sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        requestCount.ShouldBe(3);
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
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            return requestCount switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                2 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        });

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
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            return requestCount switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                2 => new HttpResponseMessage(HttpStatusCode.MultiStatus)
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
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        });

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

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
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

    #endregion
}
