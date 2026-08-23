using System.Globalization;
using System.Buffers;
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>
/// HttpClient-based CalDAV client for Calendar Object Resources.
/// Handles PROPFIND, REPORT, GET, PUT, DELETE verbs with XML/iCalendar encoding.
/// </summary>
internal sealed class CalDavClient : ICalendarClient, ICalendarCreateTransport, ICalendarMoveResourceTransport
{
    private const int MaximumCalendarResourceBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod ReportMethod = new("REPORT");

    private readonly HttpClient _httpClient;
    private readonly IOptions<CalDavOptions> _options;
    private readonly ILogger<CalDavClient> _logger;
    private readonly CalendarQueryCapabilityState _queryCapabilities;
    private readonly ConcurrentDictionary<CapabilityKey, CapabilityState> _capabilities = new();
    private readonly object _configurationGate = new();
    private int _configurationFingerprint;
    private long _capabilityGeneration;

    public CalDavClient(
        HttpClient httpClient,
        IOptions<CalDavOptions> options,
        ILogger<CalDavClient> logger,
        CalendarQueryCapabilityState queryCapabilities)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _queryCapabilities = queryCapabilities;
        _configurationFingerprint = GetConfigurationFingerprint(options.Value);
    }

    internal CalDavClient(HttpClient httpClient, IOptions<CalDavOptions> options, ILogger<CalDavClient> logger)
        : this(httpClient, options, logger, new CalendarQueryCapabilityState())
    {
    }

    public void RediscoverCapabilities()
    {
        EnsureCapabilityConfiguration();
        lock (_configurationGate)
        {
            InvalidateCapabilityObservations();
            _queryCapabilities.Invalidate();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Discovery);

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
    internal async Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
        string calendarHref,
        CalendarEntityKind entityKind,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Fetch);
        EnsureCapabilityConfiguration();
        var generation = Volatile.Read(ref _capabilityGeneration);
        if (!TryCanonicalizeCalendarHref(
                new Uri(_options.Value.BaseUrl, UriKind.Absolute),
                calendarHref,
                out var authorizedCalendarHref))
        {
            throw new CalendarDiscoveryProtocolException("Unsafe CalDAV REPORT href.");
        }
        var authorizedCalendarUri = new Uri(authorizedCalendarHref, UriKind.Absolute);
        var response = await QueryWithCapabilitiesAsync(
            authorizedCalendarHref,
            entityKind,
            from,
            to,
            generation,
            cancellationToken);
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

    private async Task<(string Content, Uri RequestUri)> QueryWithCapabilitiesAsync(
        string calendarHref,
        CalendarEntityKind entityKind,
        DateTimeOffset? from,
        DateTimeOffset? to,
        long generation,
        CancellationToken cancellationToken)
    {
        var isFiltered = (from, to) is ({ }, { });
        var minimalKey = CapabilityKey.Query(calendarHref, entityKind, filtered: false);
        var filteredKey = CapabilityKey.Query(calendarHref, entityKind, filtered: true);
        try
        {
            if (isFiltered && IsUnavailable(filteredKey))
                throw new CalendarQueryFilterUnsupportedException();
            if (!isFiltered && IsUnavailable(minimalKey))
                throw new CalendarDiscoveryUnsupportedCapabilityException("The minimal Calendar query capability is unavailable.");

            var response = await SendReportAsync(
                calendarHref,
                DavRequestBuilder.BuildCalendarEntityQuery(entityKind, from, to),
                cancellationToken);
            ObserveCapability(isFiltered ? filteredKey : minimalKey, CapabilityState.Verified, generation);
            return response;
        }
        catch (CalendarQueryFilterUnsupportedException) when (isFiltered)
        {
            ObserveCapability(filteredKey, CapabilityState.Unavailable, generation);
            try
            {
                if (IsUnavailable(minimalKey))
                    throw new CalendarDiscoveryUnsupportedCapabilityException("The minimal Calendar query capability is unavailable.");
                var response = await SendReportAsync(
                    calendarHref,
                    DavRequestBuilder.BuildCalendarEntityQuery(entityKind),
                    cancellationToken);
                ObserveCapability(minimalKey, CapabilityState.Verified, generation);
                return response;
            }
            catch (CalendarQueryFilterUnsupportedException exception)
            {
                ObserveCapability(minimalKey, CapabilityState.Unavailable, generation);
                throw new CalendarDiscoveryUnsupportedCapabilityException(exception.Message);
            }
        }
        catch (CalendarQueryFilterUnsupportedException exception)
        {
            ObserveCapability(minimalKey, CapabilityState.Unavailable, generation);
            throw new CalendarDiscoveryUnsupportedCapabilityException(exception.Message);
        }
    }

    /// <inheritdoc />
    public Task<CalendarResourceRead> GetCalendarResourceAsync(
        string href,
        CancellationToken cancellationToken) => GetCalendarResourceAsync(
            href,
            CalendarHttpTelemetry.IsAbsenceProbe,
            cancellationToken);

    /// <inheritdoc />
    public Task<CalendarResourceRead> ProbeCalendarResourceAbsenceAsync(
        string href,
        CancellationToken cancellationToken) => GetCalendarResourceAsync(
            href,
            absenceProbe: true,
            cancellationToken);

    async Task<CalendarResourceRead> ICalendarMoveResourceTransport.ReadMoveResourceAsync(
        string authorizedCalendarHref,
        string href,
        bool absenceProbe,
        CancellationToken cancellationToken)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Fetch);
        if (!TryValidateMoveResourceIdentity(
                authorizedCalendarHref,
                href,
                out var calendarUri,
                out var resourceUri))
        {
            return new CalendarResourceRead(CalendarResourceReadCode.InvalidInput, href);
        }

        using var response = await SendGetWithRedirectHandlingAsync(
            resourceUri,
            absenceProbe,
            cancellationToken,
            authorizedCalendar: calendarUri,
            allowInCalendarRedirect: true).ConfigureAwait(false);
        return await ReadCalendarResourceResponseAsync(resourceUri, response, cancellationToken).ConfigureAwait(false);
    }

    async Task<CalendarResourceRead> ICalendarMoveResourceTransport.ProbeMoveResourcePresenceAsync(
        string authorizedCalendarHref,
        string href,
        CancellationToken cancellationToken)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Fetch);
        if (!TryValidateMoveResourceIdentity(
                authorizedCalendarHref,
                href,
                out var calendarUri,
                out var resourceUri))
        {
            return new CalendarResourceRead(CalendarResourceReadCode.InvalidInput, href);
        }

        using var response = await SendGetWithRedirectHandlingAsync(
            resourceUri,
            absenceProbe: true,
            cancellationToken,
            authorizedCalendar: calendarUri,
            allowInCalendarRedirect: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new CalendarResourceRead(CalendarResourceReadCode.NotFound, resourceUri.AbsoluteUri);
        response.EnsureSuccessStatusCode();
        return new CalendarResourceRead(CalendarResourceReadCode.Success, resourceUri.AbsoluteUri);
    }

    private async Task<CalendarResourceRead> GetCalendarResourceAsync(
        string href,
        bool absenceProbe,
        CancellationToken cancellationToken)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Fetch);
        if (!TryValidateAbsoluteResourceHref(href, out var resourceUri))
            return new CalendarResourceRead(CalendarResourceReadCode.InvalidInput);

        using var response = await SendGetWithRedirectHandlingAsync(
            resourceUri,
            absenceProbe,
            cancellationToken);
        return await ReadCalendarResourceResponseAsync(resourceUri, response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CalendarResourceRead> GetCalendarResourceDirectlyForQueryAsync(
        string calendarHref,
        string resourceHref,
        CancellationToken cancellationToken)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Fetch);
        var origin = new Uri(_options.Value.BaseUrl, UriKind.Absolute);
        if (!TryCanonicalizeCalendarHref(origin, calendarHref, out var canonicalCalendarHref)
            || !TryValidateAbsoluteResourceHref(resourceHref, out var resourceUri)
            || !IsDirectResourceOf(new Uri(canonicalCalendarHref, UriKind.Absolute), resourceUri))
        {
            return new CalendarResourceRead(CalendarResourceReadCode.InvalidInput, resourceHref);
        }

        using var response = await SendGetWithRedirectHandlingAsync(
            resourceUri,
            absenceProbe: false,
            cancellationToken,
            authorizedCalendar: new Uri(canonicalCalendarHref, UriKind.Absolute)).ConfigureAwait(false);
        return await ReadCalendarResourceResponseAsync(resourceUri, response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CalendarResourceRead> ReadCalendarResourceResponseAsync(
        Uri resourceUri,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new CalendarResourceRead(CalendarResourceReadCode.NotFound, resourceUri.AbsoluteUri);

        response.EnsureSuccessStatusCode();
        var responseEntityTag = response.Headers.ETag;
        var bounded = await ReadBoundedContentAsync(response.Content, cancellationToken);
        if (bounded.Content is null)
            return new CalendarResourceRead(
                CalendarResourceReadCode.PayloadTooLarge,
                resourceUri.AbsoluteUri,
                ObservedByteCount: bounded.ObservedByteCount);
        var content = bounded.Content;
        try
        {
            _ = StrictUtf8.GetCharCount(content);
        }
        catch (DecoderFallbackException)
        {
            return new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError, resourceUri.AbsoluteUri);
        }

        if (responseEntityTag is null || responseEntityTag.IsWeak)
        {
            return new CalendarResourceRead(
                CalendarResourceReadCode.ConcurrencyUnavailable,
                resourceUri.AbsoluteUri,
                AuthoritativeUtf8: content);
        }

        var entityTag = responseEntityTag.ToString();
        return CalendarResourceRead.Success(resourceUri.AbsoluteUri, entityTag, content);
    }

    internal async Task<IReadOnlyList<CalendarResourceRead>> GetCalendarResourcesForQueryAsync(
        string calendarHref,
        IReadOnlyList<string> hrefs,
        CancellationToken cancellationToken)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Fetch);
        if (hrefs.Count == 0)
            return [];
        EnsureCapabilityConfiguration();
        if (!TryCreateMultigetPlan(calendarHref, hrefs, out var canonicalCalendarHref, out var resourceUris))
        {
            return hrefs.Select(href => new CalendarResourceRead(
                CalendarResourceReadCode.InvalidInput,
                ResourceHref: href)).ToArray();
        }
        var calendarUri = new Uri(canonicalCalendarHref, UriKind.Absolute);

        var capability = _queryCapabilities.ObserveContext(_options.Value, canonicalCalendarHref);
        if (_queryCapabilities.IsUnavailable(capability))
            throw new CalendarDiscoveryUnsupportedCapabilityException("The Calendar multiget capability is unavailable.");

        (IReadOnlyList<CalendarMultigetResource> Resources, Uri RequestUri) response;
        try
        {
            response = await SendMultigetReportAsync(
                canonicalCalendarHref,
                DavRequestBuilder.BuildCalendarMultiget(resourceUris.Select(resourceUri => resourceUri.AbsoluteUri).ToArray()),
                resourceUris.Count,
                cancellationToken);
        }
        catch (CalendarMultigetUnsupportedException exception)
        {
            _queryCapabilities.ObserveUnavailable(capability);
            throw new CalendarDiscoveryUnsupportedCapabilityException(exception.Message);
        }
        var requested = resourceUris.Select(resourceUri => resourceUri.AbsoluteUri).ToHashSet(StringComparer.Ordinal);
        var returned = new Dictionary<string, CalendarMultigetResource>(StringComparer.Ordinal);
        foreach (var candidate in response.Resources)
        {
            if (!TryAddMultigetResource(response.RequestUri, calendarUri, requested, returned, candidate))
                throw new CalendarDiscoveryProtocolException("The Calendar multiget response contained an unsafe resource href.");
        }
        return resourceUris.Select(resourceUri => ToCalendarResourceRead(
                resourceUri.AbsoluteUri,
                returned.GetValueOrDefault(resourceUri.AbsoluteUri)))
            .ToArray();
    }

    private bool TryCreateMultigetPlan(
        string calendarHref,
        IReadOnlyList<string> hrefs,
        out string canonicalCalendarHref,
        out IReadOnlyList<Uri> resourceUris)
    {
        canonicalCalendarHref = string.Empty;
        resourceUris = [];
        var origin = new Uri(_options.Value.BaseUrl, UriKind.Absolute);
        if (hrefs.Count > CalendarQueryPolicy.MaximumMultigetBatchSize
            || !TryCanonicalizeCalendarHref(origin, calendarHref, out canonicalCalendarHref))
            return false;

        var calendarUri = new Uri(canonicalCalendarHref, UriKind.Absolute);
        var validated = new List<Uri>(hrefs.Count);
        foreach (var href in hrefs)
        {
            if (!TryValidateAbsoluteResourceHref(href, out var resourceUri)
                || !IsDirectResourceOf(calendarUri, resourceUri))
                return false;
            validated.Add(resourceUri);
        }
        resourceUris = validated;
        return true;
    }

    private bool TryAddMultigetResource(
        Uri responseUri,
        Uri calendarUri,
        IReadOnlySet<string> requested,
        IDictionary<string, CalendarMultigetResource> returned,
        CalendarMultigetResource candidate)
    {
        if (!TryCanonicalizeResourceHref(responseUri, candidate.Href, out var canonical)
            || !IsDirectResourceOf(calendarUri, new Uri(canonical, UriKind.Absolute))
            || !requested.Contains(canonical))
            return false;
        return returned.TryAdd(canonical, candidate);
    }

    private static CalendarResourceRead ToCalendarResourceRead(
        string resourceHref,
        CalendarMultigetResource? resource)
    {
        if (resource?.StatusCode == (int)HttpStatusCode.NotFound)
            return new CalendarResourceRead(CalendarResourceReadCode.NotFound, ResourceHref: resourceHref);
        if (resource is null)
            return new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError, ResourceHref: resourceHref);
        if (resource.StatusCode is < 200 or > 299 || resource.CalendarData is null)
            return new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError, ResourceHref: resourceHref);

        var normalized = resource.CalendarData.ReplaceLineEndings("\r\n");
        var byteCount = StrictUtf8.GetByteCount(normalized);
        if (byteCount > MaximumCalendarResourceBytes)
        {
            return new CalendarResourceRead(
                CalendarResourceReadCode.PayloadTooLarge,
                ResourceHref: resourceHref,
                ObservedByteCount: byteCount);
        }
        var content = StrictUtf8.GetBytes(normalized);
        if (!EntityTagHeaderValue.TryParse(resource.EntityTag, out var entityTag) || entityTag.IsWeak)
        {
            return new CalendarResourceRead(
                CalendarResourceReadCode.ConcurrencyUnavailable,
                ResourceHref: resourceHref,
                AuthoritativeUtf8: content);
        }
        return CalendarResourceRead.Success(resourceHref, entityTag.ToString(), content);
    }

    /// <inheritdoc />
    public async Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
        CalendarResourceCreateRequest request,
        CancellationToken cancellationToken)
    {
        var key = CapabilityKey.Mutation(
            _options.Value.BaseUrl,
            request.CalendarHref,
            request.ResourceHref,
            "create");
        return await ExecuteCapabilityOperationAsync(
            key,
            () => new CalendarResourceCreateResult(CalendarResourceCreateCode.UnsupportedCapability, request.ResourceHref),
            () => new CalendarResourceCreateProtocol(_httpClient, new Uri(_options.Value.BaseUrl, UriKind.Absolute))
                .CreateAsync(request, cancellationToken),
            result => result.Code == CalendarResourceCreateCode.UnsupportedCapability,
            result => result.Code == CalendarResourceCreateCode.Dispatched);
    }

    /// <inheritdoc />
    public async Task<CalendarResourceDeleteDispatchResult> DeleteCalendarResourceAsync(
        CalendarResourceDeleteRequest request,
        CancellationToken cancellationToken) => await ExecuteCapabilityOperationAsync(
            CapabilityKey.Mutation(_options.Value.BaseUrl, ParentHref(request.ResourceHref), request.ResourceHref, "delete"),
            () => new CalendarResourceDeleteDispatchResult(CalendarResourceDeleteDispatchCode.UnsupportedCapability),
            () => new CalendarResourceDeleteProtocol(_httpClient, new Uri(_options.Value.BaseUrl, UriKind.Absolute))
                .DeleteAsync(request, cancellationToken),
            result => result.Code == CalendarResourceDeleteDispatchCode.UnsupportedCapability,
            result => result.Code == CalendarResourceDeleteDispatchCode.Dispatched);

    /// <inheritdoc />
    public async Task<CalendarResourceMoveDispatchResult> MoveCalendarResourceAsync(
        CalendarResourceMoveDispatchRequest request,
        CancellationToken cancellationToken) => await DispatchMoveAsync(
        ParentHref(request.SourceHref),
        ParentHref(request.DestinationHref),
        request,
        cancellationToken);

    Task<CalendarResourceMoveDispatchResult> ICalendarMoveResourceTransport.DispatchMoveAsync(
        string sourceCalendarHref,
        string destinationCalendarHref,
        CalendarResourceMoveDispatchRequest request,
        CancellationToken cancellationToken) => DispatchMoveAsync(
        sourceCalendarHref,
        destinationCalendarHref,
        request,
        cancellationToken);

    private async Task<CalendarResourceMoveDispatchResult> DispatchMoveAsync(
        string? sourceCalendarHref,
        string? destinationCalendarHref,
        CalendarResourceMoveDispatchRequest request,
        CancellationToken cancellationToken) => await ExecuteCapabilityOperationAsync(
            CapabilityKey.Mutation(
                _options.Value.BaseUrl,
                $"{sourceCalendarHref}->{destinationCalendarHref}",
                request.SourceHref,
                "move"),
            () => new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.UnsupportedCapability),
            () => new CalendarResourceMoveProtocol(_httpClient, new Uri(_options.Value.BaseUrl, UriKind.Absolute))
                .MoveAsync(request, sourceCalendarHref, destinationCalendarHref, cancellationToken),
            result => result.Code == CalendarResourceMoveDispatchCode.UnsupportedCapability,
            result => result.Code == CalendarResourceMoveDispatchCode.Dispatched);

    /// <inheritdoc />
    public async Task<CalendarResourceUpdateDispatchResult> UpdateCalendarResourceAsync(
        CalendarResourceUpdateRequest request,
        CancellationToken cancellationToken) => await ExecuteCapabilityOperationAsync(
            CapabilityKey.Mutation(_options.Value.BaseUrl, ParentHref(request.ResourceHref), request.ResourceHref, "update"),
            () => new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.UnsupportedCapability),
            () => new CalendarResourceUpdateProtocol(_httpClient, new Uri(_options.Value.BaseUrl, UriKind.Absolute))
                .UpdateAsync(request, cancellationToken),
            result => result.Code == CalendarResourceUpdateDispatchCode.UnsupportedCapability,
            result => result.Code == CalendarResourceUpdateDispatchCode.Dispatched);

    private async Task<TResult> ExecuteCapabilityOperationAsync<TResult>(
        CapabilityKey key,
        Func<TResult> unavailableResult,
        Func<Task<TResult>> operation,
        Func<TResult, bool> isUnavailable,
        Func<TResult, bool> isVerified)
    {
        EnsureCapabilityConfiguration();
        var generation = Volatile.Read(ref _capabilityGeneration);
        if (IsUnavailable(key))
            return unavailableResult();

        var result = await operation();
        if (isUnavailable(result))
            ObserveCapability(key, CapabilityState.Unavailable, generation);
        else if (isVerified(result))
            ObserveCapability(key, CapabilityState.Verified, generation);
        return result;
    }

    private static string? ParentHref(string resourceHref) =>
        Uri.TryCreate(resourceHref, UriKind.Absolute, out var resourceUri)
            ? new Uri(resourceUri, ".").AbsoluteUri
            : null;

    private async Task<HttpResponseMessage> SendGetWithRedirectHandlingAsync(
        Uri initialUri,
        bool absenceProbe,
        CancellationToken cancellationToken,
        int maxRedirects = 5,
        Uri? authorizedCalendar = null,
        bool allowInCalendarRedirect = false)
    {
        var currentUri = initialUri;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            MarkReadPurpose(request, absenceProbe);
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var redirect = GetValidatedReadRedirect(
                response,
                currentUri,
                initialUri,
                authorizedCalendar,
                allowInCalendarRedirect,
                attempt,
                maxRedirects);
            if (redirect is null)
            {
                EnsureQueryResponseIdentity(
                    response,
                    currentUri,
                    initialUri,
                    authorizedCalendar,
                    allowInCalendarRedirect);
                return response;
            }
            response.Dispose();
            currentUri = redirect;
        }
    }

    private Uri? GetValidatedReadRedirect(
        HttpResponseMessage response,
        Uri currentUri,
        Uri initialUri,
        Uri? authorizedCalendar,
        bool allowInCalendarRedirect,
        int attempt,
        int maxRedirects)
    {
        if (response.StatusCode == HttpStatusCode.RedirectMethod)
            throw ReadRedirectProtocol(response, "A 303 redirect is invalid for a CalDAV resource read.");
        if (!IsPreservingReadRedirect(response.StatusCode))
            return null;
        if (attempt == maxRedirects)
            throw ReadRedirectProtocol(response, "CalDAV redirect limit was exceeded.");
        var location = response.Headers.Location
            ?? throw ReadRedirectProtocol(response, "A CalDAV redirect is missing its Location header.");
        if (location.OriginalString.Contains("%2e", StringComparison.OrdinalIgnoreCase))
            throw ReadRedirectProtocol(response, "Unsafe CalDAV resource redirect href.");
        var redirectUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        if (!TryValidateAbsoluteResourceHref(redirectUri.AbsoluteUri, out var canonicalRedirectUri))
            throw ReadRedirectProtocol(response, "Unsafe CalDAV resource redirect href.");
        if (authorizedCalendar is not null
            && !IsAuthorizedReadResource(
                authorizedCalendar,
                initialUri,
                canonicalRedirectUri,
                allowInCalendarRedirect))
        {
            throw ReadRedirectProtocol(response, "A query resource redirect changed resource identity.");
        }
        return canonicalRedirectUri;
    }

    private static void EnsureQueryResponseIdentity(
        HttpResponseMessage response,
        Uri currentUri,
        Uri initialUri,
        Uri? authorizedCalendar,
        bool allowInCalendarRedirect)
    {
        if (authorizedCalendar is null
            || IsAuthorizedReadResource(
                authorizedCalendar,
                initialUri,
                response.RequestMessage?.RequestUri ?? currentUri,
                allowInCalendarRedirect))
        {
            return;
        }
        throw ReadRedirectProtocol(response, "A query resource response changed resource identity.");
    }

    private static CalendarDiscoveryProtocolException ReadRedirectProtocol(
        HttpResponseMessage response,
        string message)
    {
        response.Dispose();
        return new CalendarDiscoveryProtocolException(message);
    }

    private static void MarkReadPurpose(HttpRequestMessage request, bool absenceProbe)
    {
        if (absenceProbe)
            CalendarHttpTelemetry.MarkAbsenceProbe(request);
        else if (CalendarHttpTelemetry.IsQueryResourceRead)
            CalendarHttpTelemetry.MarkQueryResourceRead(request);
    }

    private static bool IsPreservingReadRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsAuthorizedQueryResource(Uri calendar, Uri requested, Uri candidate) =>
        IsDirectResourceOf(calendar, candidate)
        && string.Equals(requested.AbsoluteUri, candidate.AbsoluteUri, StringComparison.Ordinal);

    private static bool IsAuthorizedReadResource(
        Uri calendar,
        Uri requested,
        Uri candidate,
        bool allowInCalendarRedirect) => IsDirectResourceOf(calendar, candidate)
        && (allowInCalendarRedirect
            || string.Equals(requested.AbsoluteUri, candidate.AbsoluteUri, StringComparison.Ordinal));

    private bool TryValidateMoveResourceIdentity(
        string authorizedCalendarHref,
        string href,
        out Uri calendarUri,
        out Uri resourceUri)
    {
        calendarUri = null!;
        resourceUri = null!;
        var origin = new Uri(_options.Value.BaseUrl, UriKind.Absolute);
        if (!TryCanonicalizeCalendarHref(origin, authorizedCalendarHref, out var canonicalCalendarHref)
            || !TryValidateAbsoluteResourceHref(href, out resourceUri))
        {
            return false;
        }
        calendarUri = new Uri(canonicalCalendarHref, UriKind.Absolute);
        return IsDirectResourceOf(calendarUri, resourceUri);
    }

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
        CancellationToken cancellationToken,
        int maximumBytes = MaximumCalendarResourceBytes)
    {
        var gzipEncoded = content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase);
        if (!gzipEncoded && content.Headers.ContentLength > maximumBytes)
            return new BoundedContentRead(null, SaturateByteCount(content.Headers.ContentLength.Value));

        await using var encodedStream = await content.ReadAsStreamAsync(cancellationToken);
        await using Stream stream = gzipEncoded
            ? new GZipStream(encodedStream, CompressionMode.Decompress, leaveOpen: false)
            : encodedStream;
        using var destination = new MemoryStream(
            !gzipEncoded && content.Headers.ContentLength is >= 0 && content.Headers.ContentLength <= maximumBytes
                ? (int)content.Headers.ContentLength.Value
                : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (destination.Length <= maximumBytes)
            {
                var remainingPlusOne = (maximumBytes - (int)destination.Length) + 1;
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remainingPlusOne)),
                    cancellationToken);
                if (read == 0)
                    return new BoundedContentRead(destination.ToArray(), (int)destination.Length);
                if (destination.Length + read > maximumBytes)
                    return new BoundedContentRead(null, (int)destination.Length + read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return new BoundedContentRead(null, maximumBytes + 1);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int SaturateByteCount(long byteCount) => byteCount > int.MaxValue ? int.MaxValue : (int)byteCount;

    private sealed record BoundedContentRead(byte[]? Content, int ObservedByteCount);

    private async Task<string?> DiscoverCalendarHomeSetAsync(CancellationToken cancellationToken, bool failOnNotFound = false)
    {
        var principalBody = DavRequestBuilder.BuildPropFindCalendarHomeSet();
        var principalHref = "/.well-known/caldav";

        var suppressUpstreamFailures = !failOnNotFound;
        var configuredResult = await TryDiscoverFromBaseUrlAsync(
            principalBody,
            depth: 0,
            cancellationToken,
            suppressUpstreamFailures);
        if (IsConfiguredCalendarHome(configuredResult.HomeSet))
            return configuredResult.HomeSet;

        var wellKnownResult = await TryDiscoverFromPathAsync(principalHref, principalBody, depth: 0, cancellationToken, suppressUpstreamFailures);
        if (wellKnownResult.HomeSet is not null)
            return wellKnownResult.HomeSet;

        if (wellKnownResult.PrincipalUrl is null)
        {
            if (configuredResult.HomeSet is not null)
                return configuredResult.HomeSet;

            if (configuredResult.PrincipalUrl is null)
            {
                _logger.LogWarning(
                    "CalDAV operation {Code} failed at {Phase}",
                    "home_not_found",
                    "selectionDiscoveryCapability");
                if (failOnNotFound)
                    throw new CalendarDiscoveryProtocolException("Calendar-home-set was not discovered.");
                return null;
            }

            wellKnownResult = configuredResult;
        }

        var discoveredHomeSet = await TryDiscoverFromPrincipalAsync(wellKnownResult.PrincipalUrl!, principalBody, depth: 0, cancellationToken, suppressUpstreamFailures);
        if (discoveredHomeSet is null && failOnNotFound)
            throw new CalendarDiscoveryProtocolException("Calendar-home-set was not discovered.");
        return discoveredHomeSet;
    }

    private bool IsConfiguredCalendarHome(string? discoveredHomeSet)
    {
        if (discoveredHomeSet is null)
            return false;

        var configuredText = _options.Value.BaseUrl;
        if (!Uri.TryCreate(configuredText, UriKind.Absolute, out var configuredUri)
            || !IsSafeCanonicalUri(configuredUri, configuredText)
            || !TryCanonicalizeCalendarHref(configuredUri, discoveredHomeSet, out var canonicalHomeSet))
        {
            return false;
        }

        return string.Equals(
            configuredUri.AbsolutePath.TrimEnd('/'),
            new Uri(canonicalHomeSet, UriKind.Absolute).AbsolutePath.TrimEnd('/'),
            StringComparison.Ordinal)
            && string.Equals(configuredUri.Query, new Uri(canonicalHomeSet, UriKind.Absolute).Query, StringComparison.Ordinal);
    }

    private async Task<(string? HomeSet, string? PrincipalUrl)> TryDiscoverFromPathAsync(
        string path,
        string body,
        int depth,
        CancellationToken cancellationToken,
        bool suppressUpstreamFailures,
        bool allowUnsupportedEndpoint = false)
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
        catch (HttpRequestException exception) when (
            suppressUpstreamFailures
            || exception.StatusCode == HttpStatusCode.NotFound
            || allowUnsupportedEndpoint && exception.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            _logger.LogDebug(
                "CalDAV operation {Code} failed at {Phase}",
                "endpoint_unavailable",
                "selectionDiscoveryCapability");
            return (null, null);
        }
    }

    private async Task<(string? HomeSet, string? PrincipalUrl)> TryDiscoverFromBaseUrlAsync(
        string body, int depth, CancellationToken cancellationToken, bool suppressUpstreamFailures)
    {
        var baseUrl = _options.Value.BaseUrl.TrimEnd('/');
        var uri = new Uri(baseUrl);
        var path = uri.AbsolutePath.TrimEnd('/') + "/";
        return await TryDiscoverFromPathAsync(
            path,
            body,
            depth,
            cancellationToken,
            suppressUpstreamFailures,
            allowUnsupportedEndpoint: true);
    }

    private async Task<string?> TryDiscoverFromPrincipalAsync(
        string principalUrl, string body, int depth, CancellationToken cancellationToken, bool suppressUpstreamFailures)
    {
        try
        {
            _logger.LogDebug(
                "CalDAV operation {Code} entered {Phase}",
                "principal_discovery",
                "selectionDiscoveryCapability");
            var response = await SendPropFindAsync(principalUrl, body, depth, cancellationToken);
            var homeSet = DavResponseParser.ParseCalendarHomeSet(response.Content);
            if (homeSet is null)
                return null;
            if (!TryCanonicalizeCalendarHref(response.RequestUri, homeSet, out var canonicalHomeSet))
                throw new CalendarDiscoveryProtocolException("Unsafe calendar-home-set href.");
            return canonicalHomeSet;
        }
        catch (HttpRequestException exception) when (
            suppressUpstreamFailures || exception.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "CalDAV operation {Code} failed at {Phase}",
                "principal_unavailable",
                "selectionDiscoveryCapability");
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
                if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
                    || response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
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

    private async Task<(IReadOnlyList<CalendarMultigetResource> Resources, Uri RequestUri)> SendMultigetReportAsync(
        string href,
        string body,
        int requestedResourceCount,
        CancellationToken cancellationToken)
    {
        if (!TryCanonicalizeCalendarHref(new Uri(_options.Value.BaseUrl, UriKind.Absolute), href, out var canonicalHref))
            throw new CalendarDiscoveryProtocolException("Unsafe CalDAV REPORT href.");

        var request = new HttpRequestMessage(ReportMethod, canonicalHref)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        CalendarHttpTelemetry.MarkQueryMultiget(request, requestedResourceCount);

        using var response = await SendWithRedirectHandlingAsync(request, body, cancellationToken, ensureSuccess: false);
        var requestUri = response.RequestMessage?.RequestUri ?? new Uri(canonicalHref, UriKind.Absolute);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
                throw new CalendarMultigetUnsupportedException();
            var bounded = await ReadBoundedContentAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (bounded.Content is not null
                && response.StatusCode is (HttpStatusCode.BadRequest or HttpStatusCode.Forbidden))
            {
                string errorBody;
                try
                {
                    errorBody = StrictUtf8.GetString(bounded.Content);
                }
                catch (DecoderFallbackException)
                {
                    throw new CalendarDiscoveryProtocolException("The Calendar multiget error response was not valid UTF-8.");
                }
                if (DavResponseParser.IsCalendarMultigetUnsupportedError(errorBody))
                    throw new CalendarMultigetUnsupportedException();
            }
            response.EnsureSuccessStatusCode();
        }
        var resources = await CalendarMultigetResponseParser.ParseAsync(
                response.Content,
                requestedResourceCount,
                cancellationToken)
            .ConfigureAwait(false);
        return (resources, requestUri);
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

    private sealed class CalendarMultigetUnsupportedException : Exception
    {
    }

    private bool IsUnavailable(CapabilityKey key) =>
        _capabilities.TryGetValue(key, out var state) && state == CapabilityState.Unavailable;

    private void EnsureCapabilityConfiguration()
    {
        var fingerprint = GetConfigurationFingerprint(_options.Value);
        if (fingerprint == _configurationFingerprint)
            return;

        lock (_configurationGate)
        {
            if (fingerprint == _configurationFingerprint)
                return;
            InvalidateCapabilityObservations();
            _queryCapabilities.Invalidate();
            _configurationFingerprint = fingerprint;
        }
    }

    private void InvalidateCapabilityObservations()
    {
        Interlocked.Increment(ref _capabilityGeneration);
        _capabilities.Clear();
    }

    private void ObserveCapability(CapabilityKey key, CapabilityState state, long generation)
    {
        lock (_configurationGate)
        {
            if (generation == _capabilityGeneration)
                _capabilities[key] = state;
        }
    }

    private static int GetConfigurationFingerprint(CalDavOptions options) => HashCode.Combine(
        options.BaseUrl,
        options.Username,
        options.Password,
        options.CalendarHrefs,
        options.DefaultEventCalendarName,
        options.DefaultTodoCalendarName);

    private enum CapabilityState
    {
        Verified,
        Unavailable
    }

    private sealed record CapabilityKey(string Origin, string CalendarHref, string? ResourceHref, string Operation)
    {
        public static CapabilityKey Query(string calendarHref, CalendarEntityKind kind, bool filtered)
        {
            var calendar = new Uri(calendarHref, UriKind.Absolute);
            return new CapabilityKey(
                calendar.GetLeftPart(UriPartial.Authority),
                calendar.AbsoluteUri,
                null,
                $"calendar-query:{kind}:{(filtered ? "filtered" : "minimal")}");
        }

        public static CapabilityKey CalendarMultiget(string calendarHref)
        {
            var calendar = new Uri(calendarHref, UriKind.Absolute);
            return new CapabilityKey(
                calendar.GetLeftPart(UriPartial.Authority),
                calendar.AbsoluteUri,
                null,
                "calendar-multiget");
        }

        public static CapabilityKey Mutation(
            string baseUrl,
            string? calendarHref,
            string resourceHref,
            string operation) => new(
                new Uri(baseUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority),
                calendarHref ?? string.Empty,
                resourceHref,
                operation);
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

        _logger.LogDebug(
            "CalDAV operation {Code} entered {Phase}",
            "safe_redirect",
            "execution");
        var redirectRequest = new HttpRequestMessage(method, canonicalRedirectUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };
        foreach (var depthValue in depthValues)
            redirectRequest.Headers.Add("Depth", depthValue);
        if (currentRequest.Options.TryGetValue(
                CalendarHttpTelemetry.MultigetResourceCountKey,
                out var multigetResourceCount))
        {
            CalendarHttpTelemetry.MarkQueryMultiget(redirectRequest, multigetResourceCount);
        }
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

    private bool TryCanonicalizeCalendarHref(Uri homeSetUri, string href, out string canonicalHref)
    {
        canonicalHref = string.Empty;
        if (HasUnsafeEncodedPath(href)
            || !Uri.TryCreate(homeSetUri, href, out var candidate)
            || !candidate.IsAbsoluteUri
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || !string.IsNullOrEmpty(candidate.Query))
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

    private static bool HasUnsafeEncodedPath(string href) =>
        href.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        || href.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        || href.Contains("%5c", StringComparison.OrdinalIgnoreCase);

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

}
