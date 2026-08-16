using System.Diagnostics;
using System.Globalization;
using System.Buffers;
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
internal sealed class CalDavClient : ICalDavClient, ICalendarClient
{
    private const int MaximumCalendarResourceBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
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
        var response = await SendPropFindAsync(homeSetHref, propfindBody, depth: 1, cancellationToken);

        var taskLists = DavResponseParser.ParseTaskLists(response.Content);

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

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("caldav.get_calendars", ActivityKind.Client);

        var homeSetHref = await DiscoverCalendarHomeSetAsync(cancellationToken, failOnNotFound: true);
        if (homeSetHref is null)
            return [];

        var configuredBaseUri = new Uri(_options.Value.BaseUrl, UriKind.Absolute);
        if (!TryCanonicalizeCalendarHref(configuredBaseUri, homeSetHref, out var canonicalHomeSetHref))
            throw new CalendarDiscoveryProtocolException("Unsafe calendar-home-set href.");

        var propfindBody = DavRequestBuilder.BuildPropFindCalendarProperties();
        var response = await SendPropFindAsync(canonicalHomeSetHref, propfindBody, depth: 1, cancellationToken);
        var homeSetUri = response.RequestUri;

        var calendars = new List<CalendarDescriptor>();
        foreach (var calendar in DavResponseParser.ParseCalendars(response.Content))
        {
            if (TryCanonicalizeCalendarHref(homeSetUri, calendar.Href, out var canonicalHref))
                calendars.Add(calendar with { Href = canonicalHref });
            else
                throw new CalendarDiscoveryProtocolException("Unsafe Calendar href.");
        }

        return calendars
            .OrderBy(calendar => calendar.Href, StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> QueryCalendarResourceHrefsAsync(
        string calendarHref,
        CalendarEntityKind entityKind,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (!TryCanonicalizeCalendarHref(
                new Uri(_options.Value.BaseUrl, UriKind.Absolute),
                calendarHref,
                out var authorizedCalendarHref))
        {
            throw new CalendarDiscoveryProtocolException("Unsafe CalDAV REPORT href.");
        }
        var authorizedCalendarUri = new Uri(authorizedCalendarHref, UriKind.Absolute);
        var body = DavRequestBuilder.BuildCalendarEntityQuery(entityKind, from, to);
        (string Content, Uri RequestUri) response;
        try
        {
            response = await SendReportAsync(calendarHref, body, cancellationToken);
        }
        catch (CalendarQueryFilterUnsupportedException) when (from is not null && to is not null)
        {
            try
            {
                response = await SendReportAsync(
                    calendarHref,
                    DavRequestBuilder.BuildCalendarEntityQuery(entityKind),
                    cancellationToken);
            }
            catch (CalendarQueryFilterUnsupportedException exception)
            {
                throw new CalendarDiscoveryUnsupportedCapabilityException(exception.Message);
            }
        }
        catch (CalendarQueryFilterUnsupportedException exception)
        {
            throw new CalendarDiscoveryUnsupportedCapabilityException(exception.Message);
        }
        var calendarUri = response.RequestUri;
        var hrefs = new List<string>();
        foreach (var candidateHref in DavResponseParser.ParseCalendarResourceHrefs(response.Content))
        {
            if (IsCollectionSelfHref(calendarUri, candidateHref))
                continue;
            if (!TryCanonicalizeResourceHref(calendarUri, candidateHref, out var canonicalHref))
                throw new CalendarDiscoveryProtocolException("Unsafe Calendar Object Resource candidate href.");
            if (!IsDirectResourceOf(authorizedCalendarUri, new Uri(canonicalHref, UriKind.Absolute)))
                throw new CalendarDiscoveryProtocolException("Calendar Object Resource candidate escaped its authorized Calendar identity.");
            hrefs.Add(canonicalHref);
        }

        return hrefs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public async Task<CalendarResourceRead> GetCalendarResourceAsync(string href, CancellationToken cancellationToken)
    {
        if (!TryValidateAbsoluteResourceHref(href, out var resourceUri))
            return new CalendarResourceRead(CalendarResourceReadCode.InvalidInput);

        using var response = await SendGetWithRedirectHandlingAsync(resourceUri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new CalendarResourceRead(CalendarResourceReadCode.NotFound);

        response.EnsureSuccessStatusCode();
        var responseEntityTag = response.Headers.ETag;
        if (responseEntityTag is null || responseEntityTag.IsWeak)
            return new CalendarResourceRead(CalendarResourceReadCode.ConcurrencyUnavailable);

        var entityTag = responseEntityTag.ToString();
        var bounded = await ReadBoundedContentAsync(response.Content, cancellationToken);
        if (bounded.Content is null)
            return new CalendarResourceRead(CalendarResourceReadCode.PayloadTooLarge, ObservedByteCount: bounded.ObservedByteCount);
        var content = bounded.Content;
        try
        {
            _ = StrictUtf8.GetCharCount(content);
        }
        catch (DecoderFallbackException)
        {
            return new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError);
        }

        return CalendarResourceRead.Success(resourceUri.AbsoluteUri, entityTag, content);
    }

    /// <inheritdoc />
    public async Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
        CalendarResourceCreateRequest request,
        CancellationToken cancellationToken) => await new CalendarResourceCreateProtocol(
            _httpClient,
            new Uri(_options.Value.BaseUrl, UriKind.Absolute)).CreateAsync(request, cancellationToken);

    private async Task<HttpResponseMessage> SendGetWithRedirectHandlingAsync(
        Uri initialUri,
        CancellationToken cancellationToken,
        int maxRedirects = 5)
    {
        var currentUri = initialUri;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.RedirectMethod)
            {
                response.Dispose();
                throw new CalendarDiscoveryProtocolException("A 303 redirect is invalid for a CalDAV resource read.");
            }

            if (!IsPreservingReadRedirect(response.StatusCode))
                return response;
            if (attempt == maxRedirects)
            {
                response.Dispose();
                throw new CalendarDiscoveryProtocolException("CalDAV redirect limit was exceeded.");
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                response.Dispose();
                throw new CalendarDiscoveryProtocolException("A CalDAV redirect is missing its Location header.");
            }
            if (location.OriginalString.Contains("%2e", StringComparison.OrdinalIgnoreCase))
            {
                response.Dispose();
                throw new CalendarDiscoveryProtocolException("Unsafe CalDAV resource redirect href.");
            }
            var redirectUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (!TryValidateAbsoluteResourceHref(redirectUri.AbsoluteUri, out var canonicalRedirectUri))
            {
                response.Dispose();
                throw new CalendarDiscoveryProtocolException("Unsafe CalDAV resource redirect href.");
            }

            response.Dispose();
            currentUri = canonicalRedirectUri;
        }
    }

    private static bool IsPreservingReadRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private bool TryValidateAbsoluteResourceHref(string href, out Uri resourceUri)
    {
        resourceUri = null!;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var candidate) || !IsSafeCanonicalUri(candidate, href))
            return false;

        var origin = new Uri(_options.Value.BaseUrl, UriKind.Absolute);
        if (!HasSameOrigin(origin, candidate))
            return false;

        resourceUri = candidate;
        return true;
    }

    private static bool IsSafeCanonicalUri(Uri candidate, string original) =>
        (candidate.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        && string.IsNullOrEmpty(candidate.UserInfo)
        && string.IsNullOrEmpty(candidate.Fragment)
        && string.IsNullOrEmpty(candidate.Query)
        && !candidate.AbsolutePath.Contains("%2F", StringComparison.OrdinalIgnoreCase)
        && !candidate.AbsolutePath.Contains("%5C", StringComparison.OrdinalIgnoreCase)
        && !original.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.AbsoluteUri, original, StringComparison.Ordinal);

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static async Task<BoundedContentRead> ReadBoundedContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumCalendarResourceBytes)
            return new BoundedContentRead(null, SaturateByteCount(content.Headers.ContentLength.Value));

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(
            content.Headers.ContentLength is >= 0 and <= MaximumCalendarResourceBytes
                ? (int)content.Headers.ContentLength.Value
                : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (destination.Length <= MaximumCalendarResourceBytes)
            {
                var remainingPlusOne = (MaximumCalendarResourceBytes - (int)destination.Length) + 1;
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remainingPlusOne)),
                    cancellationToken);
                if (read == 0)
                    return new BoundedContentRead(destination.ToArray(), (int)destination.Length);
                if (destination.Length + read > MaximumCalendarResourceBytes)
                    return new BoundedContentRead(null, (int)destination.Length + read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return new BoundedContentRead(null, MaximumCalendarResourceBytes + 1);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int SaturateByteCount(long byteCount) => byteCount > int.MaxValue ? int.MaxValue : (int)byteCount;

    private sealed record BoundedContentRead(byte[]? Content, int ObservedByteCount);

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
        var responseXml = (await SendReportAsync(taskListHref, reportBody, cancellationToken)).Content;

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

    private async Task<string?> DiscoverCalendarHomeSetAsync(CancellationToken cancellationToken, bool failOnNotFound = false)
    {
        var principalBody = DavRequestBuilder.BuildPropFindCalendarHomeSet();
        var principalHref = "/.well-known/caldav";

        var suppressUpstreamFailures = !failOnNotFound;
        var wellKnownResult = await TryDiscoverFromPathAsync(principalHref, principalBody, depth: 0, cancellationToken, suppressUpstreamFailures);
        if (wellKnownResult.HomeSet is not null)
            return wellKnownResult.HomeSet;

        if (wellKnownResult.PrincipalUrl is null)
        {
            var baseUrlResult = await TryDiscoverFromBaseUrlAsync(principalBody, depth: 0, cancellationToken, suppressUpstreamFailures);
            if (baseUrlResult.HomeSet is not null)
                return baseUrlResult.HomeSet;

            if (baseUrlResult.PrincipalUrl is null)
            {
                _logger.LogWarning("Failed to discover calendar-home-set from {BaseUrl}", _options.Value.BaseUrl);
                if (failOnNotFound)
                    throw new CalendarDiscoveryProtocolException("Calendar-home-set was not discovered.");
                return null;
            }

            wellKnownResult = baseUrlResult;
        }

        var discoveredHomeSet = await TryDiscoverFromPrincipalAsync(wellKnownResult.PrincipalUrl!, principalBody, depth: 0, cancellationToken, suppressUpstreamFailures);
        if (discoveredHomeSet is null && failOnNotFound)
            throw new CalendarDiscoveryProtocolException("Calendar-home-set was not discovered.");
        return discoveredHomeSet;
    }

    private async Task<(string? HomeSet, string? PrincipalUrl)> TryDiscoverFromPathAsync(
        string path, string body, int depth, CancellationToken cancellationToken, bool suppressUpstreamFailures)
    {
        try
        {
            var response = await SendPropFindAsync(path, body, depth, cancellationToken);
            var homeSet = DavResponseParser.ParseCalendarHomeSet(response.Content);
            if (homeSet is not null)
            {
                if (!TryCanonicalizeCalendarHref(response.RequestUri, homeSet, out var canonicalHomeSet))
                    throw new CalendarDiscoveryProtocolException("Unsafe calendar-home-set href.");
                return (canonicalHomeSet, null);
            }

            var principalUrl = DavResponseParser.ParseCurrentUserPrincipal(response.Content);
            if (principalUrl is null)
                return (null, null);
            if (!TryCanonicalizeCalendarHref(response.RequestUri, principalUrl, out var canonicalPrincipal))
                throw new CalendarDiscoveryProtocolException("Unsafe current-user-principal href.");
            return (null, canonicalPrincipal);
        }
        catch (HttpRequestException exception) when (suppressUpstreamFailures || exception.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("CalDAV path not found: {Path}", path);
            return (null, null);
        }
    }

    private async Task<(string? HomeSet, string? PrincipalUrl)> TryDiscoverFromBaseUrlAsync(
        string body, int depth, CancellationToken cancellationToken, bool suppressUpstreamFailures)
    {
        var baseUrl = _options.Value.BaseUrl.TrimEnd('/');
        var uri = new Uri(baseUrl);
        var path = uri.AbsolutePath.TrimEnd('/') + "/";
        return await TryDiscoverFromPathAsync(path, body, depth, cancellationToken, suppressUpstreamFailures);
    }

    private async Task<string?> TryDiscoverFromPrincipalAsync(
        string principalUrl, string body, int depth, CancellationToken cancellationToken, bool suppressUpstreamFailures)
    {
        try
        {
            _logger.LogDebug("PROPFIND principal at {PrincipalUrl} for calendar-home-set", principalUrl);
            var response = await SendPropFindAsync(principalUrl, body, depth, cancellationToken);
            var homeSet = DavResponseParser.ParseCalendarHomeSet(response.Content);
            if (homeSet is null)
                return null;
            if (!TryCanonicalizeCalendarHref(response.RequestUri, homeSet, out var canonicalHomeSet))
                throw new CalendarDiscoveryProtocolException("Unsafe calendar-home-set href.");
            return canonicalHomeSet;
        }
        catch (HttpRequestException ex) when (suppressUpstreamFailures || ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(ex, "Failed to discover calendar-home-set from principal at {PrincipalUrl}", principalUrl);
            return null;
        }
    }

    private async Task<(string Content, Uri RequestUri)> SendPropFindAsync(string href, string body, int depth, CancellationToken cancellationToken)
    {
        if (!TryCanonicalizeCalendarHref(new Uri(_options.Value.BaseUrl, UriKind.Absolute), href, out var canonicalHref))
            throw new CalendarDiscoveryProtocolException("Unsafe CalDAV discovery href.");

        var request = new HttpRequestMessage(PropFindMethod, canonicalHref)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));

        using var response = await SendWithRedirectHandlingAsync(request, body, cancellationToken);
        var requestUri = response.RequestMessage?.RequestUri ?? new Uri(canonicalHref, UriKind.Absolute);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return (content, requestUri);
    }

    private async Task<(string Content, Uri RequestUri)> SendReportAsync(
        string href,
        string body,
        CancellationToken cancellationToken)
    {
        if (!TryCanonicalizeCalendarHref(new Uri(_options.Value.BaseUrl, UriKind.Absolute), href, out var canonicalHref))
            throw new CalendarDiscoveryProtocolException("Unsafe CalDAV REPORT href.");

        var request = new HttpRequestMessage(ReportMethod, canonicalHref)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", "1");

        using var response = await SendWithRedirectHandlingAsync(request, body, cancellationToken, ensureSuccess: false);
        var bounded = await ReadBoundedContentAsync(response.Content, cancellationToken);
        if (bounded.Content is null)
        {
            throw new HttpRequestException(
                "The CalDAV REPORT response exceeded the safe payload limit.",
                null,
                HttpStatusCode.RequestEntityTooLarge);
        }
        try
        {
            var content = StrictUtf8.GetString(bounded.Content);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
                    && DavResponseParser.IsSupportedFilterError(content))
                    throw new CalendarQueryFilterUnsupportedException();
                response.EnsureSuccessStatusCode();
            }
            return (content, response.RequestMessage?.RequestUri ?? new Uri(canonicalHref, UriKind.Absolute));
        }
        catch (DecoderFallbackException)
        {
            throw new CalendarDiscoveryProtocolException("The CalDAV REPORT response was not valid UTF-8.");
        }
    }

    /// <summary>
    /// Sends a request and follows redirect responses (301, 302, 307, 308)
    /// manually, preserving the original HTTP method and body.
    /// This is necessary because auto-redirect is disabled — CalDAV methods
    /// like PROPFIND and REPORT must be preserved across redirects.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRedirectHandlingAsync(
        HttpRequestMessage originalRequest,
        string body,
        CancellationToken cancellationToken,
        int maxRedirects = 5,
        bool ensureSuccess = true)
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
            response = await _httpClient.SendAsync(
                currentRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.RequestMessage = currentRequest;

            var redirectRequest = CreateRedirectRequest(response, currentRequest, method, body, contentType, depthValues, attempt, maxRedirects);
            if (redirectRequest is null)
                break;
            currentRequest = redirectRequest;
        }

        if (!ensureSuccess)
            return response!;
        try
        {
            response!.EnsureSuccessStatusCode();
        }
        catch
        {
            response!.Dispose();
            throw;
        }
        return response;
    }

    private sealed class CalendarQueryFilterUnsupportedException : Exception
    {
    }

    private HttpRequestMessage? CreateRedirectRequest(
        HttpResponseMessage response,
        HttpRequestMessage currentRequest,
        HttpMethod method,
        string body,
        string contentType,
        IReadOnlyList<string> depthValues,
        int attempt,
        int maxRedirects)
    {
        if (response.StatusCode == HttpStatusCode.RedirectMethod)
        {
            response.Dispose();
            throw new CalendarDiscoveryProtocolException("A 303 redirect is invalid for a CalDAV method.");
        }
        var redirectUrl = GetRedirectUrl(response, currentRequest.RequestUri);
        if (redirectUrl is null)
            return null;
        if (attempt == maxRedirects)
        {
            response.Dispose();
            throw new CalendarDiscoveryProtocolException("CalDAV redirect limit was exceeded.");
        }
        if (!TryCanonicalizeCalendarHref(new Uri(_options.Value.BaseUrl, UriKind.Absolute), redirectUrl, out var canonicalRedirectUrl))
        {
            response.Dispose();
            throw new CalendarDiscoveryProtocolException("Unsafe CalDAV redirect href.");
        }

        _logger.LogDebug("{Method} redirect {StatusCode} within the configured origin", method, (int)response.StatusCode);
        var redirectRequest = new HttpRequestMessage(method, canonicalRedirectUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };
        foreach (var depthValue in depthValues)
            redirectRequest.Headers.Add("Depth", depthValue);
        return redirectRequest;
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

    private bool TryCanonicalizeCalendarHref(Uri homeSetUri, string href, out string canonicalHref)
    {
        canonicalHref = string.Empty;
        if (!Uri.TryCreate(homeSetUri, href, out var candidate)
            || !candidate.IsAbsoluteUri
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        var configuredBaseUri = new Uri(_options.Value.BaseUrl, UriKind.Absolute);
        if (!string.Equals(candidate.Scheme, configuredBaseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.Host, configuredBaseUri.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != configuredBaseUri.Port)
        {
            return false;
        }

        canonicalHref = candidate.AbsoluteUri;
        return true;
    }

    private bool TryCanonicalizeResourceHref(Uri calendarUri, string href, out string canonicalHref)
    {
        canonicalHref = string.Empty;
        var absoluteInput = href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (href.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(calendarUri, href, out var candidate)
            || !IsCanonicalReportCandidate(candidate, href, absoluteInput))
        {
            return false;
        }

        var calendarPath = calendarUri.AbsolutePath.EndsWith('/')
            ? calendarUri.AbsolutePath
            : calendarUri.AbsolutePath + '/';
        if (!candidate.AbsolutePath.StartsWith(calendarPath, StringComparison.Ordinal))
            return false;
        var relative = candidate.AbsolutePath[calendarPath.Length..];
        if (relative.Length == 0 || relative.Contains('/'))
            return false;

        canonicalHref = candidate.AbsoluteUri;
        return true;
    }

    private bool IsCanonicalReportCandidate(Uri candidate, string href, bool absoluteInput) =>
        IsSafeCanonicalUri(candidate, candidate.AbsoluteUri)
        && (!absoluteInput || string.Equals(candidate.AbsoluteUri, href, StringComparison.Ordinal))
        && HasSameOrigin(new Uri(_options.Value.BaseUrl, UriKind.Absolute), candidate);

    private static bool IsCollectionSelfHref(Uri calendarUri, string href) =>
        Uri.TryCreate(calendarUri, href, out var candidate)
        && HasSameOrigin(calendarUri, candidate)
        && IsSafeCanonicalUri(candidate, candidate.AbsoluteUri)
        && string.Equals(
            candidate.AbsolutePath.TrimEnd('/'),
            calendarUri.AbsolutePath.TrimEnd('/'),
            StringComparison.Ordinal)
        && string.Equals(candidate.Query, calendarUri.Query, StringComparison.Ordinal);

    private static bool IsDirectResourceOf(Uri calendarUri, Uri resourceUri)
    {
        if (!HasSameOrigin(calendarUri, resourceUri))
            return false;
        var calendarPath = calendarUri.AbsolutePath.EndsWith('/')
            ? calendarUri.AbsolutePath
            : calendarUri.AbsolutePath + '/';
        if (!resourceUri.AbsolutePath.StartsWith(calendarPath, StringComparison.Ordinal))
            return false;
        var relativePath = resourceUri.AbsolutePath[calendarPath.Length..];
        return relativePath.Length > 0 && !relativePath.Contains('/');
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
