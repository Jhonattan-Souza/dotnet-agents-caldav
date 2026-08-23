using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Applies configured Calendar Scope to standards-based Calendar discovery.</summary>
internal sealed class CalendarService : ICalendarService
{
    private readonly ICalendarClient _calendarClient;
    private readonly CalendarOperationDiscovery _operationDiscovery;
    private readonly IOptions<CalDavOptions> _options;
    private readonly ILogger<CalendarService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ICalendarEntityIdentityGenerator _identityGenerator;
    private readonly CalendarDiscoveryPolicy _discoveryPolicy;

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
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
        _identityGenerator = identityGenerator;
        _discoveryPolicy = new CalendarDiscoveryPolicy(options, logger);
        _operationDiscovery = new CalendarOperationDiscovery(
            calendarClient,
            options,
            _discoveryPolicy.ApplyScope,
            _discoveryPolicy.ResolveDefault);
        _calendarClient = _operationDiscovery;
    }

    /// <inheritdoc />
    public async Task<CalendarDiscoveryResult> GetCalendarsAsync(CancellationToken cancellationToken)
    {
        _calendarClient.RediscoverCapabilities();
        return await _operationDiscovery.GetScopedResultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CalendarSelectionResult> ResolveDefaultCalendarAsync(
        CalendarEntityKind entityKind,
        CancellationToken cancellationToken)
    {
        _ = await _calendarClient.GetCalendarsAsync(cancellationToken);
        return _operationDiscovery.ResolveDefault(entityKind);
    }

    private CalendarSelectionResult ResolveDefaultCalendar(
        CalendarEntityKind entityKind,
        IReadOnlyList<CalendarDescriptor> discovered,
        IReadOnlyList<CalendarDescriptor> scoped) => _operationDiscovery.ResolveDefault(entityKind);

    /// <inheritdoc />
    public async Task<CalendarOccurrenceQueryResult> QueryOccurrencesAsync(
        CalendarOccurrenceQuery query,
        CancellationToken cancellationToken) => await new CalendarOccurrenceQueryEngine(
            _calendarClient,
            _options.Value,
            ApplyScope,
            ResolveDefaultCalendar).QueryAsync(query, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarTodoQueryResult> QueryTodosAsync(
        CalendarTodoQuery query,
        CancellationToken cancellationToken) => await new CalendarTodoQueryEngine(
            _calendarClient,
            _options.Value,
            ApplyScope,
            ResolveDefaultCalendar).QueryAsync(query, cancellationToken);

    /// <inheritdoc />
    public Task<CalendarResourceRead> GetResourceAsync(string href, CancellationToken cancellationToken) =>
        GetResourceAsync(href, expectedKind: null, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarExactResourceResult> ExactCreateResourceAsync(
        CalendarExactCreateRequest request,
        CancellationToken cancellationToken)
    {
        var module = CreationModule();
        var review = await module.ReviewExactAsync(new ExactCreateIntent(request), cancellationToken);
        if (review.Outcome is not null)
            return review.Outcome;
        return RequireExact(await module.CreateAsync(
            new CalendarCreationCommand.Exact(review.ReviewedCreate!),
            cancellationToken));
    }

    /// <inheritdoc />
    public async Task<CalendarExactResourceResult> ExactCreateResourceAsync(
        CalendarReviewedExactCreate reviewedCreate,
        CancellationToken cancellationToken) => RequireExact(await CreationModule().CreateAsync(
            new CalendarCreationCommand.Exact(reviewedCreate),
            cancellationToken));

    /// <inheritdoc />
    public async Task<CalendarExactCreateReviewResult> ReviewExactCreateResourceAsync(
        CalendarExactCreateRequest request,
        CancellationToken cancellationToken) => await CreationModule().ReviewExactAsync(
            new ExactCreateIntent(request),
            cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarExactResourceResult> ExactReplaceResourceAsync(
        CalendarExactReplaceRequest request,
        CancellationToken cancellationToken) => await ExactResourceEngine().ReplaceAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarExactResourceReviewResult> ReviewExactReplaceResourceAsync(
        CalendarExactReplaceRequest request,
        CancellationToken cancellationToken) => await ExactResourceEngine().ReviewReplaceAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarExactResourceResult> ExactMoveResourceAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken) => await ExactResourceEngine().MoveAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarExactResourceReviewResult> ReviewExactMoveResourceAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken) => await ExactResourceEngine().ReviewMoveAsync(request, cancellationToken);

    private async Task<CalendarResourceRead> GetResourceAsync(
        string href,
        CalendarEntityKind? expectedKind,
        CancellationToken cancellationToken)
    {
        if (!TryGetCanonicalResourceUri(href, out var resourceUri))
            return new CalendarResourceRead(CalendarResourceReadCode.InvalidInput);

        var configuredScope = CalendarDiscoveryPolicy.ParseScope(_options.Value.CalendarHrefs);
        if (configuredScope.Count > 0 && !configuredScope.Any(calendarHref => IsDirectResourceOf(resourceUri, calendarHref)))
            return new CalendarResourceRead(CalendarResourceReadCode.OutsideScope);

        var calendars = ApplyScope(await _calendarClient.GetCalendarsAsync(cancellationToken)).Items;
        var calendar = calendars
            .Where(candidate => IsDirectResourceOf(resourceUri, candidate.Href))
            .OrderByDescending(candidate => candidate.Href.Length)
            .FirstOrDefault();
        if (calendar is null)
            return new CalendarResourceRead(CalendarResourceReadCode.OutsideScope);
        if (expectedKind is not null && !CalendarDiscoveryPolicy.SupportsEntityKind(calendar, expectedKind.Value))
            return new CalendarResourceRead(CalendarResourceReadCode.UnsupportedCapability);

        return await CreateSnapshotAsync(calendar, href, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CalendarEntityCreateResult> CreateEventAsync(
        CalendarEventCreateRequest request,
        CancellationToken cancellationToken) => RequireSemantic(await CreationModule().CreateAsync(
            new CalendarCreationCommand.Event(request),
            cancellationToken));

    /// <inheritdoc />
    public async Task<CalendarEntityCreateResult> CreateTodoAsync(
        CalendarTodoCreateRequest request,
        CancellationToken cancellationToken) => RequireSemantic(await CreationModule().CreateAsync(
            new CalendarCreationCommand.Todo(request),
            cancellationToken));

    /// <inheritdoc />
    public async Task<CalendarEntityPatchResult> PatchEventAsync(
        CalendarEventPatchRequest request,
        CancellationToken cancellationToken) => await PatchEntityEngine().PatchEventAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarEntityPatchResult> PatchTodoAsync(
        CalendarTodoPatchRequest request,
        CancellationToken cancellationToken) => await PatchEntityEngine().PatchTodoAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarEntityPatchReviewResult> ReviewEventPatchAsync(
        CalendarEventPatchRequest request,
        CancellationToken cancellationToken) => await PatchEntityEngine().ReviewEventPatchAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarEntityPatchReviewResult> ReviewTodoPatchAsync(
        CalendarTodoPatchRequest request,
        CancellationToken cancellationToken) => await PatchEntityEngine().ReviewTodoPatchAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarEntityPatchResult> AddOccurrenceAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken) => await PatchEntityEngine().AddOccurrenceAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarEntityPatchResult> ExcludeOccurrenceAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken) => await PatchEntityEngine().ExcludeOccurrenceAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarEntityPatchResult> RestoreOccurrenceExclusionAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken) => await PatchEntityEngine().RestoreOccurrenceExclusionAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarEntityPatchResult> CancelOccurrenceAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken) => await PatchEntityEngine().CancelOccurrenceAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarEntityPatchResult> RestoreOccurrenceCancellationAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken) => await PatchEntityEngine().RestoreOccurrenceCancellationAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarEntityPatchResult> CompleteTodoAsync(
        CalendarTodoCompletionRequest request,
        CancellationToken cancellationToken) => await PatchEntityEngine().CompleteTodoAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarResourceMoveResult> MoveResourceAsync(
        CalendarResourceMoveRequest request,
        CancellationToken cancellationToken) => await new CalendarResourceMoveEngine(
            _calendarClient,
            _options.Value,
            _timeProvider,
            ApplyScope,
            ResolveDefaultCalendar,
            GetResourceAsync).MoveAsync(request, cancellationToken);

    /// <inheritdoc />
    public async Task<CalendarResourceDeleteResult> DeleteResourceAsync(
        CalendarResourceRevisionReference revision,
        CancellationToken cancellationToken) => await new CalendarResourceDeleteEngine(
            _calendarClient,
            GetResourceAsync,
            ProbeResourceAbsenceAsync,
            _timeProvider).DeleteAsync(revision, cancellationToken);

    private async Task<CalendarResourceRead> ProbeResourceAbsenceAsync(
        string href,
        CancellationToken cancellationToken)
    {
        using var scope = CalendarHttpTelemetry.BeginAbsenceProbe();
        return await GetResourceAsync(href, cancellationToken);
    }

    private CalendarCreationModule CreationModule() => new(
        _calendarClient as ICalendarCreateTransport ?? new CalendarClientCreateTransport(_calendarClient),
        _options.Value,
        _timeProvider,
        _identityGenerator,
        ApplyScope,
        ResolveDefaultCalendar);

    private static CalendarEntityCreateResult RequireSemantic(CalendarCreationOutcome outcome) => outcome switch
    {
        CalendarCreationOutcome.Semantic semantic => semantic.Result,
        _ => throw new InvalidOperationException("Semantic Create returned an Exact outcome.")
    };

    private static CalendarExactResourceResult RequireExact(CalendarCreationOutcome outcome) => outcome switch
    {
        CalendarCreationOutcome.Exact exact => exact.Result,
        _ => throw new InvalidOperationException("Exact Create returned a Semantic outcome.")
    };

    private CalendarEntityPatchEngine PatchEntityEngine() => new(
        _calendarClient,
        (href, kind, cancellationToken) => GetResourceAsync(href, kind, cancellationToken),
        _timeProvider);

    private CalendarExactResourceEngine ExactResourceEngine() => new(
        _calendarClient,
        _options.Value,
        _timeProvider,
        ApplyScope);

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

    private CalendarDiscoveryResult ApplyScope(IReadOnlyList<CalendarDescriptor> discovered) =>
        _discoveryPolicy.ApplyScope(discovered);
}
