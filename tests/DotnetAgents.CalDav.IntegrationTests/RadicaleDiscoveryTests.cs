using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

/// <summary>
/// Diagnostic tests to verify the Radicale container setup and CalDAV discovery.
/// </summary>
[Collection("RadicaleCollection")]
public class RadicaleDiscoveryTests(RadicaleFixture fixture) : IAsyncLifetime
{
    private HttpClient _diagClient = null!;

    public ValueTask InitializeAsync()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        _diagClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(fixture.BaseUrl)
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("caldavtest:caldavtest123"));
        _diagClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _diagClient.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Radicale_WellKnownCaldav_HandlesRedirect()
    {
        const string propfindBody = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:prop>
                <c:calendar-home-set />
                <d:current-user-principal />
              </d:prop>
            </d:propfind>
            """;

        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/.well-known/caldav")
        {
            Content = new StringContent(propfindBody, Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", "0");

        var response = await _diagClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Follow redirect manually since AllowAutoRedirect=false
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectKeepVerb or HttpStatusCode.TemporaryRedirect)
        {
            var location = response.Headers.Location?.ToString();
            location.ShouldNotBeNull("Expected Location header in redirect");
            response.Dispose();

            request = new HttpRequestMessage(new HttpMethod("PROPFIND"), location)
            {
                Content = new StringContent(propfindBody, Encoding.UTF8, "application/xml")
            };
            request.Headers.Add("Depth", "0");
            response = await _diagClient.SendAsync(request, TestContext.Current.CancellationToken);
        }

        response.StatusCode.ShouldBe(HttpStatusCode.MultiStatus,
            $"Expected 207 from /.well-known/caldav endpoint, got {response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        (body.Contains("calendar-home-set", StringComparison.OrdinalIgnoreCase) ||
         body.Contains("current-user-principal", StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue("Expected calendar-home-set or current-user-principal in response");
    }

    [Fact]
    public async Task Radicale_TaskCollectionExists_And_HasVtodo()
    {
        const string propfindBody = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/" xmlns:a="http://apple.com/ns/ical/">
              <d:prop>
                <d:displayname />
                <d:resourcetype />
                <c:supported-calendar-component-set />
                <d:description />
                <a:calendar-color />
                <cs:getctag />
              </d:prop>
            </d:propfind>
            """;

        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), fixture.TaskListHref)
        {
            Content = new StringContent(propfindBody, Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", "0");

        var response = await _diagClient.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.MultiStatus,
            $"Status: {response.StatusCode}, Path: {fixture.TaskListHref}");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Contains("VTODO", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
            $"Expected VTODO support in task collection response: {body}");
    }

    [Fact]
    public async Task Radicale_CalendarHomeSet_DiscoverableViaProductionCode()
    {
        var taskLists = await fixture.TaskService.GetTaskListsAsync(TestContext.Current.CancellationToken);

        taskLists.ShouldNotBeEmpty();
        taskLists.ShouldContain(
            taskList => taskList.Href.TrimEnd('/') == fixture.TaskListHref.TrimEnd('/'),
            $"Expected task list '{fixture.TaskListHref}' to be discovered via production code.");
    }
}
