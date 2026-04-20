using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>
/// HttpClient-based CalDAV client focused on VTODO operations.
/// Handles PROPFIND, REPORT, GET, PUT, DELETE verbs with XML/iCalendar encoding.
/// </summary>
internal sealed class CalDavClient : ICalDavClient
{
    private static readonly ActivitySource ActivitySource = new("DotnetAgents.CalDav", "0.1.0");
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod ReportMethod = new("REPORT");

    private readonly HttpClient _httpClient;
    private readonly IOptions<CalDavOptions> _options;
    private readonly ILogger<CalDavClient> _logger;

    public CalDavClient(HttpClient httpClient, IOptions<CalDavOptions> options, ILogger<CalDavClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TaskList>> GetTaskListsAsync(CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("caldav.get_task_lists", ActivityKind.Client);

        _logger.LogDebug("Discovering task lists from {BaseUrl}", _options.Value.BaseUrl);

        // Step 1: Find the calendar-home-set for the current user
        var homeSetHref = await DiscoverCalendarHomeSetAsync(cancellationToken);
        if (homeSetHref is null)
        {
            _logger.LogWarning("Could not discover calendar-home-set for {BaseUrl}", _options.Value.BaseUrl);
            return [];
        }

        // Step 2: PROPFIND the calendar-home-set to list calendars (Depth: 1 to return child collections)
        var propfindBody = DavRequestBuilder.BuildPropFindCalendarProperties();
        var responseXml = await SendPropFindAsync(homeSetHref, propfindBody, depth: 1, cancellationToken);

        var taskLists = DavResponseParser.ParseTaskLists(responseXml);

        // Step 3: Filter to only calendars supporting VTODO
        var filtered = taskLists
            .Where(tl => tl.SupportedComponents.Count == 0 || tl.SupportedComponents.Contains("VTODO", StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Step 4: Apply optional TaskLists filter from configuration
        var configuredFilter = _options.Value.TaskLists;
        if (!string.IsNullOrWhiteSpace(configuredFilter))
        {
            var allowedHrefs = configuredFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            filtered = filtered.Where(tl => allowedHrefs.Any(allowed => tl.Href.Contains(allowed, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        _logger.LogInformation("Discovered {Count} task list(s)", filtered.Count);
        return filtered;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TaskItem>> GetTasksAsync(string taskListHref, TaskQuery query, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("caldav.get_tasks", ActivityKind.Client);
        activity?.SetTag("caldav.task_list_href", taskListHref);

        _logger.LogDebug("Querying tasks from {TaskListHref}", taskListHref);

        // Use server-side REPORT filtering for status when possible
        var reportBody = query.Status switch
        {
            Models.TaskStatus.Completed => DavRequestBuilder.BuildCalendarQuery(completedOnly: true),
            _ => DavRequestBuilder.BuildCalendarQuery()
        };
        var responseXml = await SendReportAsync(taskListHref, reportBody, cancellationToken);

        var calendarDataItems = DavResponseParser.ParseCalendarData(responseXml);
        var tasks = new List<TaskItem>();

        foreach (var (href, etag, iCalData) in calendarDataItems)
        {
            AddMatchingTasks(tasks, href, etag, iCalData, query);
        }

        _logger.LogDebug("Found {Count} task(s) in {TaskListHref}", tasks.Count, taskListHref);
        return tasks;
    }

    private void AddMatchingTasks(List<TaskItem> tasks, string href, string? etag, string iCalData, TaskQuery query)
    {
        try
        {
            var items = TaskItemMapper.FromICalText(iCalData, etag);
            foreach (var item in items)
            {
                var taskWithHref = item with { Href = href };
                if (MatchesQuery(taskWithHref, query))
                    tasks.Add(taskWithHref);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse iCalendar data for {Href}", href);
        }
    }

    /// <inheritdoc/>
    public async Task<TaskItem?> GetTaskAsync(string href, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("caldav.get_task", ActivityKind.Client);
        activity?.SetTag("caldav.task_href", href);

        _logger.LogDebug("Fetching task {Href}", href);

        var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(href));
        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Task not found at {Href}", href);
            return null;
        }

        response.EnsureSuccessStatusCode();

        var etag = response.Headers.ETag?.Tag?.Trim('"');
        var iCalData = await response.Content.ReadAsStringAsync(cancellationToken);

        var items = TaskItemMapper.FromICalText(iCalData, etag);
        var task = items.FirstOrDefault();

        if (task is null)
        {
            _logger.LogDebug("No VTODO component found in iCalendar data for {Href}", href);
            return null;
        }

        _logger.LogDebug("Fetched task {Uid} from {Href}", task.Uid, href);
        return task with { Href = href };
    }

    /// <inheritdoc/>
    public async Task<TaskItem> CreateTaskAsync(string taskListHref, TaskItem task, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("caldav.create_task", ActivityKind.Client);
        activity?.SetTag("caldav.task_list_href", taskListHref);

        _logger.LogDebug("Creating task in {TaskListHref}", taskListHref);

        // Generate UID and href if not provided
        var uid = string.IsNullOrEmpty(task.Uid) ? Guid.NewGuid().ToString() : task.Uid;
        var taskWithUid = task with { Uid = uid };
        var escapedUid = Uri.EscapeDataString(uid);
        var resourceHref = $"{taskListHref.TrimEnd('/')}/{escapedUid}.ics";

        var iCalText = TaskItemMapper.ToICalText(taskWithUid);

        var request = new HttpRequestMessage(HttpMethod.Put, BuildUrl(resourceHref))
        {
            Content = new StringContent(iCalText, Encoding.UTF8, "text/calendar")
        };
        request.Headers.Add("If-None-Match", "*");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Fetch the created task to get the server-assigned ETag and canonical href
        var etag = response.Headers.ETag?.Tag?.Trim('"');
        var location = response.Headers.Location?.OriginalString;
        var canonicalHref = string.IsNullOrWhiteSpace(location) ? resourceHref : BuildUrl(location);

        _logger.LogInformation("Created task {Uid} at {Href}", uid, canonicalHref);
        return taskWithUid with { ETag = etag, Href = canonicalHref };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Assumes the task href remains stable across PUT updates.
    /// If a server relocates resources on update, callers must re-discover the canonical href separately.
    /// </remarks>
    public async Task<TaskItem> UpdateTaskAsync(TaskItem task, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("caldav.update_task", ActivityKind.Client);
        activity?.SetTag("caldav.task_href", task.Href);

        _logger.LogDebug("Updating task {Uid} at {Href}", task.Uid, task.Href);

        var iCalText = TaskItemMapper.ToICalText(task);

        var request = new HttpRequestMessage(HttpMethod.Put, BuildUrl(task.Href))
        {
            Content = new StringContent(iCalText, Encoding.UTF8, "text/calendar")
        };

        // Use If-Match for optimistic concurrency when ETag is available
        AddIfMatchHeader(request, task.ETag);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        ThrowIfPreconditionFailed(response, task.Href);

        response.EnsureSuccessStatusCode();

        var etag = response.Headers.ETag?.Tag?.Trim('"') ?? task.ETag;

        _logger.LogInformation("Updated task {Uid} at {Href}", task.Uid, task.Href);
        return task with { ETag = etag };
    }

    /// <inheritdoc/>
    public async Task DeleteTaskAsync(string href, string? etag, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("caldav.delete_task", ActivityKind.Client);
        activity?.SetTag("caldav.task_href", href);

        _logger.LogDebug("Deleting task at {Href}", href);

        var request = new HttpRequestMessage(HttpMethod.Delete, BuildUrl(href));
        AddIfMatchHeader(request, etag);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        EnsureDeleteSucceeded(response, href);

        _logger.LogInformation("Deleted task at {Href}", href);
    }

    private static void AddIfMatchHeader(HttpRequestMessage request, string? etag)
    {
        if (string.IsNullOrEmpty(etag))
            return;

        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{etag}\""));
    }

    private static void EnsureDeleteSucceeded(HttpResponseMessage response, string href)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            var currentEtag = response.Headers.ETag?.Tag?.Trim('"');
            throw new CalDavConflictException(href, currentEtag);
        }

        // 404 is ok — already deleted
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;

        response.EnsureSuccessStatusCode();
    }

    private static void ThrowIfPreconditionFailed(HttpResponseMessage response, string href)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.PreconditionFailed)
            return;

        var currentEtag = response.Headers.ETag?.Tag?.Trim('"');
        throw new CalDavConflictException(href, currentEtag);
    }

    private async Task<string?> DiscoverCalendarHomeSetAsync(CancellationToken cancellationToken)
    {
        var principalBody = DavRequestBuilder.BuildPropFindCalendarHomeSet();
        var principalHref = "/.well-known/caldav";

        var wellKnownResult = await TryDiscoverFromPathAsync(principalHref, principalBody, depth: 0, cancellationToken);
        if (wellKnownResult.HomeSet is not null)
            return wellKnownResult.HomeSet;

        if (wellKnownResult.PrincipalUrl is null)
        {
            var baseUrlResult = await TryDiscoverFromBaseUrlAsync(principalBody, depth: 0, cancellationToken);
            if (baseUrlResult.HomeSet is not null)
                return baseUrlResult.HomeSet;

            if (baseUrlResult.PrincipalUrl is null)
            {
                _logger.LogWarning("Failed to discover calendar-home-set from {BaseUrl}", _options.Value.BaseUrl);
                return null;
            }

            wellKnownResult = baseUrlResult;
        }

        return await TryDiscoverFromPrincipalAsync(wellKnownResult.PrincipalUrl!, principalBody, depth: 0, cancellationToken);
    }

    private async Task<(string? HomeSet, string? PrincipalUrl)> TryDiscoverFromPathAsync(
        string path, string body, int depth, CancellationToken cancellationToken)
    {
        try
        {
            var responseXml = await SendPropFindAsync(path, body, depth, cancellationToken);
            var homeSet = DavResponseParser.ParseCalendarHomeSet(responseXml);
            if (homeSet is not null)
                return (homeSet, null);

            var principalUrl = DavResponseParser.ParseCurrentUserPrincipal(responseXml);
            return (null, principalUrl);
        }
        catch (HttpRequestException)
        {
            _logger.LogDebug("CalDAV path not found: {Path}", path);
            return (null, null);
        }
    }

    private async Task<(string? HomeSet, string? PrincipalUrl)> TryDiscoverFromBaseUrlAsync(
        string body, int depth, CancellationToken cancellationToken)
    {
        var baseUrl = _options.Value.BaseUrl.TrimEnd('/');
        var uri = new Uri(baseUrl);
        var path = uri.AbsolutePath.TrimEnd('/') + "/";
        return await TryDiscoverFromPathAsync(path, body, depth, cancellationToken);
    }

    private async Task<string?> TryDiscoverFromPrincipalAsync(
        string principalUrl, string body, int depth, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("PROPFIND principal at {PrincipalUrl} for calendar-home-set", principalUrl);
            var responseXml = await SendPropFindAsync(principalUrl, body, depth, cancellationToken);
            return DavResponseParser.ParseCalendarHomeSet(responseXml);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to discover calendar-home-set from principal at {PrincipalUrl}", principalUrl);
            return null;
        }
    }

    private async Task<string> SendPropFindAsync(string href, string body, int depth, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(PropFindMethod, BuildUrl(href))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));

        var response = await SendWithRedirectHandlingAsync(request, body, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<string> SendReportAsync(string href, string body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(ReportMethod, BuildUrl(href))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", "1");

        var response = await SendWithRedirectHandlingAsync(request, body, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Sends a request and follows redirect responses (301, 302, 307, 308)
    /// manually, preserving the original HTTP method and body.
    /// This is necessary because auto-redirect is disabled — CalDAV methods
    /// like PROPFIND and REPORT must be preserved across redirects.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRedirectHandlingAsync(
        HttpRequestMessage originalRequest, string body, CancellationToken cancellationToken, int maxRedirects = 3)
    {
        HttpResponseMessage? response = null;
        var currentRequest = originalRequest;
        var method = originalRequest.Method;
        var contentType = originalRequest.Content?.Headers.ContentType?.MediaType ?? "application/xml";
        var depthValues = originalRequest.Headers.TryGetValues("Depth", out var values)
            ? values.ToArray()
            : [];

        for (var attempt = 0; attempt <= maxRedirects; attempt++)
        {
            response?.Dispose();
            response = await _httpClient.SendAsync(currentRequest, cancellationToken);

            var redirectUrl = GetRedirectUrl(response, currentRequest.RequestUri);
            if (redirectUrl is null)
                break;

            _logger.LogDebug("{Method} redirect {StatusCode} -> {RedirectUrl}", method, (int)response.StatusCode, redirectUrl);

            currentRequest = new HttpRequestMessage(method, redirectUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            };

            foreach (var depthValue in depthValues)
            {
                currentRequest.Headers.Add("Depth", depthValue);
            }
        }

        response!.EnsureSuccessStatusCode();
        return response;
    }

    private static string? GetRedirectUrl(HttpResponseMessage response, Uri? requestUri)
    {
        if (response.StatusCode is not (
            HttpStatusCode.PermanentRedirect or
            HttpStatusCode.RedirectKeepVerb or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod))
        {
            return null;
        }

        var location = response.Headers.Location;
        if (location is null)
            return null;

        if (location.IsAbsoluteUri)
            return location.ToString();

        return requestUri is null ? null : new Uri(requestUri, location).ToString();
    }

    private string BuildUrl(string href)
    {
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return href;
        }

        var baseUri = new Uri(_options.Value.BaseUrl.TrimEnd('/') + "/");

        if (href.StartsWith('/'))
        {
            var origin = baseUri.GetLeftPart(UriPartial.Authority);
            return origin + href;
        }

        var baseWithPath = new Uri(baseUri, baseUri.AbsolutePath.TrimEnd('/') + "/");
        var resolved = new Uri(baseWithPath, href);
        return resolved.AbsoluteUri;
    }

    private static bool MatchesQuery(TaskItem task, TaskQuery query)
    {
        return MatchesStatus(task, query)
            && MatchesDueRange(task, query)
            && MatchesTextSearch(task, query)
            && MatchesCategory(task, query);
    }

    private static bool MatchesStatus(TaskItem task, TaskQuery query)
    {
        return query.Status is null || task.Status == query.Status;
    }

    private static bool MatchesDueRange(TaskItem task, TaskQuery query)
    {
        if (query.DueAfter is not null && (task.Due is null || task.Due < query.DueAfter))
            return false;

        if (query.DueBefore is not null && (task.Due is null || task.Due > query.DueBefore))
            return false;

        return true;
    }

    private static bool MatchesTextSearch(TaskItem task, TaskQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.TextSearch))
            return true;

        var search = query.TextSearch;
        var summaryMatch = task.Summary.Contains(search, StringComparison.OrdinalIgnoreCase);
        var descMatch = task.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false;
        return summaryMatch || descMatch;
    }

    private static bool MatchesCategory(TaskItem task, TaskQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.Category))
            return true;

        return task.Categories.Contains(query.Category, StringComparer.OrdinalIgnoreCase);
    }
}
