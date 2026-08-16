using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Applies configured Calendar Scope to standards-based Calendar discovery.</summary>
internal sealed class CalendarService : ICalendarService
{
    private const int MaximumDiagnostics = 32;
    private const int MaximumCalendars = 256;
    private readonly ICalendarClient _calendarClient;
    private readonly IOptions<CalDavOptions> _options;
    private readonly ILogger<CalendarService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ICalendarEntityIdentityGenerator _identityGenerator;

    public CalendarService(
        ICalendarClient calendarClient,
        IOptions<CalDavOptions> options,
        ILogger<CalendarService> logger)
        : this(calendarClient, options, logger, TimeProvider.System, new CalendarEntityIdentityGenerator())
    {
    }

    public CalendarService(
        ICalendarClient calendarClient,
        IOptions<CalDavOptions> options,
        ILogger<CalendarService> logger,
        TimeProvider timeProvider,
        ICalendarEntityIdentityGenerator identityGenerator)
    {
        _calendarClient = calendarClient;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
        _identityGenerator = identityGenerator;
    }

    /// <inheritdoc />
    public async Task<CalendarDiscoveryResult> GetCalendarsAsync(CancellationToken cancellationToken)
    {
        var discovered = await _calendarClient.GetCalendarsAsync(cancellationToken);
        return ApplyScope(discovered);
    }

    /// <inheritdoc />
    public async Task<CalendarSelectionResult> ResolveDefaultCalendarAsync(
        CalendarEntityKind entityKind,
        CancellationToken cancellationToken)
    {
        var discovered = await _calendarClient.GetCalendarsAsync(cancellationToken);
        return ResolveDefaultCalendar(entityKind, discovered, ApplyScope(discovered).Items);
    }

    private CalendarSelectionResult ResolveDefaultCalendar(
        CalendarEntityKind entityKind,
        IReadOnlyList<CalendarDescriptor> discovered,
        IReadOnlyList<CalendarDescriptor> scoped)
    {
        var authorizedCandidates = scoped.Take(MaximumDiagnostics).ToArray();
        var name = GetDefaultName(entityKind);
        if (string.IsNullOrWhiteSpace(name))
            return CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, authorizedCandidates);

        var normalizedName = name.Trim();
        var matchingDiscovered = discovered.Where(calendar =>
            string.Equals(calendar.DisplayName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingDiscovered.Length == 0)
            return CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, authorizedCandidates);

        var matchingScoped = scoped.Where(calendar =>
            string.Equals(calendar.DisplayName?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingScoped.Length == 0)
            return CalendarSelectionResult.Failure(CalendarSelectionCode.OutsideScope, authorizedCandidates);

        if (matchingScoped.Length > 1)
            return CalendarSelectionResult.Failure(CalendarSelectionCode.Ambiguous, matchingScoped.Take(MaximumDiagnostics).ToArray());

        return SupportsEntityKind(matchingScoped[0], entityKind)
            ? CalendarSelectionResult.Success(matchingScoped[0])
            : CalendarSelectionResult.Failure(CalendarSelectionCode.UnsupportedCapability, matchingScoped);
    }

    /// <inheritdoc />
    public async Task<CalendarEntityQueryResult> QueryEntitiesAsync(
        CalendarEntityQuery query,
        CancellationToken cancellationToken) => await new CalendarEntityQueryEngine(
            _calendarClient,
            _options.Value,
            ApplyScope,
            ResolveDefaultCalendar).QueryAsync(query, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarOccurrenceQueryResult> QueryOccurrencesAsync(
        CalendarOccurrenceQuery query,
        CancellationToken cancellationToken) => await new CalendarOccurrenceQueryEngine(
            _calendarClient,
            _options.Value,
            ApplyScope,
            ResolveDefaultCalendar).QueryAsync(query, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarResourceRead> GetResourceAsync(string href, CancellationToken cancellationToken)
    {
        if (!TryGetCanonicalResourceUri(href, out var resourceUri))
            return new CalendarResourceRead(CalendarResourceReadCode.InvalidInput);

        var configuredScope = ParseScope(_options.Value.CalendarHrefs);
        if (configuredScope.Count > 0 && !configuredScope.Any(calendarHref => IsDirectResourceOf(resourceUri, calendarHref)))
            return new CalendarResourceRead(CalendarResourceReadCode.OutsideScope);

        var calendars = ApplyScope(await _calendarClient.GetCalendarsAsync(cancellationToken)).Items;
        var calendar = calendars
            .Where(candidate => IsDirectResourceOf(resourceUri, candidate.Href))
            .OrderByDescending(candidate => candidate.Href.Length)
            .FirstOrDefault();
        if (calendar is null)
            return new CalendarResourceRead(CalendarResourceReadCode.OutsideScope);

        return await CreateSnapshotAsync(calendar, href, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CalendarEntityCreateResult> CreateEventAsync(
        CalendarEventCreateRequest request,
        CancellationToken cancellationToken) => await CreateEntityEngine().CreateEventAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarEntityCreateResult> CreateTodoAsync(
        CalendarTodoCreateRequest request,
        CancellationToken cancellationToken) => await CreateEntityEngine().CreateTodoAsync(request, cancellationToken);

    private CalendarEntityCreateEngine CreateEntityEngine() => new(
        _calendarClient,
        _options.Value,
        _timeProvider,
        _identityGenerator,
        ApplyScope,
        ResolveDefaultCalendar);

    private async Task<CalendarResourceRead> CreateSnapshotAsync(
        CalendarDescriptor calendar,
        string href,
        CancellationToken cancellationToken)
    {
        var read = await _calendarClient.GetCalendarResourceAsync(href, cancellationToken);
        if (read.Code != CalendarResourceReadCode.Success)
            return read;
        return CalendarResourceProjector.AttachSnapshot(calendar.Href, read);
    }

    private bool TryGetCanonicalResourceUri(string href, out Uri resourceUri)
    {
        resourceUri = null!;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var candidate)
            || (!candidate.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || !string.IsNullOrEmpty(candidate.Query)
            || HasEncodedPathSeparator(candidate)
            || !string.Equals(candidate.AbsoluteUri, href, StringComparison.Ordinal))
        {
            return false;
        }

        var origin = new Uri(_options.Value.BaseUrl, UriKind.Absolute);
        if (!HasSameOrigin(origin, candidate))
            return false;

        resourceUri = candidate;
        return true;
    }

    private static bool IsDirectResourceOf(Uri resourceUri, string calendarHref)
    {
        if (!Uri.TryCreate(calendarHref, UriKind.Absolute, out var calendarUri)
            || !HasSameOrigin(calendarUri, resourceUri)
            || !string.IsNullOrEmpty(calendarUri.UserInfo)
            || !string.IsNullOrEmpty(calendarUri.Fragment)
            || !string.IsNullOrEmpty(calendarUri.Query))
        {
            return false;
        }

        var calendarPath = calendarUri.AbsolutePath.EndsWith('/')
            ? calendarUri.AbsolutePath
            : calendarUri.AbsolutePath + '/';
        if (!resourceUri.AbsolutePath.StartsWith(calendarPath, StringComparison.Ordinal))
            return false;

        var relativePath = resourceUri.AbsolutePath[calendarPath.Length..];
        return relativePath.Length > 0 && !relativePath.Contains('/');
    }

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static bool HasEncodedPathSeparator(Uri uri) =>
        uri.AbsolutePath.Contains("%2F", StringComparison.OrdinalIgnoreCase)
        || uri.AbsolutePath.Contains("%5C", StringComparison.OrdinalIgnoreCase);

    private CalendarDiscoveryResult ApplyScope(IReadOnlyList<CalendarDescriptor> discovered)
    {
        var scope = ParseScope(_options.Value.CalendarHrefs);
        var scopedHrefs = scope.ToHashSet(StringComparer.Ordinal);
        var uniqueDiscovered = discovered
            .GroupBy(calendar => calendar.Href, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (uniqueDiscovered.Length > MaximumCalendars)
            throw new CalendarDiscoveryLimitException(uniqueDiscovered.Length);

        var scopedItems = scopedHrefs.Count == 0
            ? uniqueDiscovered
            : uniqueDiscovered.Where(calendar => scopedHrefs.Contains(calendar.Href)).ToArray();
        var uniqueItems = scopedItems
            .GroupBy(calendar => calendar.Href, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(calendar => calendar.Href, StringComparer.Ordinal)
            .ToArray();
        var diagnostics = BuildDiagnostics(scope, discovered, MaximumDiagnostics);
        var items = uniqueItems;

        _logger.LogDebug("Discovered {DiscoveredCount} Calendar(s), {InScopeCount} in scope", discovered.Count, items.Length);
        return new CalendarDiscoveryResult(items, diagnostics);
    }

    private string? GetDefaultName(CalendarEntityKind entityKind) => entityKind switch
    {
        CalendarEntityKind.Event => _options.Value.DefaultEventCalendarName,
        CalendarEntityKind.Todo => _options.Value.DefaultTodoCalendarName,
        _ => null
    };

    private static bool SupportsEntityKind(CalendarDescriptor calendar, CalendarEntityKind entityKind) => entityKind switch
    {
        CalendarEntityKind.Event => calendar.EventSupport != EntityKindSupport.NotAdvertised,
        CalendarEntityKind.Todo => calendar.TodoSupport != EntityKindSupport.NotAdvertised,
        _ => false
    };

    private static IReadOnlyList<string> ParseScope(string? calendarHrefs) =>
        calendarHrefs is null
            ? []
            : calendarHrefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

    private static IReadOnlyList<CalendarDiagnostic> BuildDiagnostics(
        IReadOnlyList<string> scope,
        IReadOnlyList<CalendarDescriptor> discovered,
        int maximumDiagnostics)
    {
        if (scope.Count == 0)
            return [];

        var duplicateHrefs = scope.GroupBy(href => href, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        var discoveredHrefs = discovered.Select(calendar => calendar.Href).ToHashSet(StringComparer.Ordinal);
        var missingHrefs = scope.Where(href => !discoveredHrefs.Contains(href))
            .Distinct(StringComparer.Ordinal);

        return duplicateHrefs.Select(_ => CreateDuplicateDiagnostic())
            .Concat(missingHrefs.Select(_ => CreateMissingDiagnostic()))
            .Take(maximumDiagnostics)
            .ToArray();
    }

    private static CalendarDiagnostic CreateDuplicateDiagnostic() =>
        new("duplicate_calendar_href", "A Calendar href is configured more than once.", CalendarDiagnosticSeverity.Warning);

    private static CalendarDiagnostic CreateMissingDiagnostic() =>
        new("calendar_href_not_found", "A configured Calendar href was not discovered.", CalendarDiagnosticSeverity.Warning);
}
