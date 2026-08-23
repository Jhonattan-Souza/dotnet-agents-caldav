using System.Net.Http.Headers;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed record CalendarQueryAcquisitionRequest(
    CalendarEntityScope Scope,
    IReadOnlyList<CalendarEntityKind> EntityKinds,
    DateTimeOffset? From,
    DateTimeOffset? To);

internal sealed record AcquiredCalendarQuery(
    IReadOnlyList<AcquiredCalendarResource> Resources,
    IReadOnlyList<QueryDiagnostic> Diagnostics,
    QueryFailure? Error)
{
    internal static AcquiredCalendarQuery Success(
        IReadOnlyList<AcquiredCalendarResource> resources,
        IReadOnlyList<QueryDiagnostic> diagnostics) => new(resources, diagnostics, null);

    internal static AcquiredCalendarQuery Failure(QueryFailure error) => new([], [], error);
}

internal sealed class CalendarQueryAcquisitionExecutor(
    Func<ICalendarQueryTransport> transportFactory,
    IOptions<CalDavOptions> options,
    CalendarQueryResourceRetriever resourceRetriever)
{
    private const int MaximumDiagnostics = 32;
    private const int MaximumResources = CalendarQuerySnapshotPolicy.MaximumItems;
    private readonly CalDavOptions _options = options.Value;

    internal async Task<AcquiredCalendarQuery> ExecuteAsync(
        CalendarQueryAcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        var prevalidation = Prevalidate(request);
        if (prevalidation is not null)
            return AcquiredCalendarQuery.Failure(prevalidation);
        var transport = transportFactory();
        CalendarQueryDiscovery discovery;
        using (CalendarQueryTelemetry.StartPhase("discovery"))
            discovery = await transport.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        discovery = ValidateDiscovery(discovery);
        var selection = Select(request, discovery);
        if (selection.Error is not null)
            return AcquiredCalendarQuery.Failure(selection.Error);
        var diagnostics = discovery.Discovery.Diagnostics.Select(ToQueryDiagnostic)
            .Concat(selection.Diagnostics)
            .Take(MaximumDiagnostics)
            .ToList();
        IReadOnlyDictionary<string, CalendarDescriptor>? candidates;
        using (CalendarQueryTelemetry.StartPhase("candidate"))
            candidates = await CollectCandidatesAsync(transport, selection.Selections, request, cancellationToken)
                .ConfigureAwait(false);
        if (candidates is null)
        {
            return AcquiredCalendarQuery.Failure(CalendarQueryFailures.Limit(
                "The query exhausted its resource budget.",
                new QueryExecutionLimits(ResourcesInspected: MaximumResources + 1)));
        }
        FetchResult fetched;
        using (CalendarQueryTelemetry.StartPhase("fetch"))
            fetched = await FetchSnapshotsAsync(transport, candidates, diagnostics, cancellationToken)
                .ConfigureAwait(false);
        return fetched.Error is null
            ? AcquiredCalendarQuery.Success(fetched.Resources, fetched.Diagnostics)
            : AcquiredCalendarQuery.Failure(fetched.Error);
    }

    private QueryFailure? Prevalidate(CalendarQueryAcquisitionRequest request)
    {
        if (!IsValid(request))
            return CalendarQueryFailures.InvalidInput();
        var href = request.Scope.Mode == CalendarEntityScopeMode.Selected ? request.Scope.Calendar?.Href : null;
        if (href is null)
            return null;
        if (!IsSafeCalendarHref(href))
            return CalendarQueryFailures.UnsafeHref();
        var scope = ParseScope(_options.CalendarHrefs);
        return scope.Count > 0 && !scope.Contains(href, StringComparer.Ordinal)
            ? CalendarQueryFailures.OutsideScope([])
            : null;
    }

    private async Task<FetchResult> FetchSnapshotsAsync(
        ICalendarQueryTransport transport,
        IReadOnlyDictionary<string, CalendarDescriptor> candidates,
        List<QueryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var retrieval = await resourceRetriever.RetrieveAsync(transport, candidates, cancellationToken)
            .ConfigureAwait(false);
        if (retrieval.Error is not null)
            return FetchResult.Failure(retrieval.Error);
        var resources = new List<AcquiredCalendarResource>();
        foreach (var group in retrieval.Resources.GroupBy(resource => resource.Calendar.Href, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var calendar = group.First().Calendar;
            var ordered = group.OrderBy(resource => resource.RequestedHref, StringComparer.Ordinal).ToArray();
            var error = AccumulateBatch(
                calendar,
                ordered.Select(resource => resource.RequestedHref).ToArray(),
                ordered.Select(resource => resource.Read).ToArray(),
                resources,
                diagnostics);
            if (error is not null)
                return FetchResult.Failure(error);
        }
        return FetchResult.Success(resources, diagnostics);
    }

    private static QueryFailure? AccumulateBatch(
        CalendarDescriptor calendar,
        IReadOnlyList<string> requested,
        IReadOnlyList<CalendarResourceRead> reads,
        ICollection<AcquiredCalendarResource> resources,
        ICollection<QueryDiagnostic> diagnostics)
    {
        if (reads.Count != requested.Count)
            return CalendarQueryFailures.Protocol();
        for (var index = 0; index < requested.Count; index++)
        {
            var read = reads[index];
            if (!string.Equals(read.ResourceHref, requested[index], StringComparison.Ordinal))
                return CalendarQueryFailures.Protocol();
            if (read.Code == CalendarResourceReadCode.NotFound)
            {
                AddDiagnostic(diagnostics);
                continue;
            }
            var error = ReadFailure(read);
            if (error is not null)
                return error;
            resources.Add(CalendarQueryResourceMaterializer.Materialize(calendar.Href, read));
            CalendarQueryTelemetry.Add("caldav.query.snapshot_count");
        }
        return null;
    }

    private static async Task<IReadOnlyDictionary<string, CalendarDescriptor>?> CollectCandidatesAsync(
        ICalendarQueryTransport transport,
        IReadOnlyList<(CalendarDescriptor Calendar, CalendarEntityKind Kind)> selections,
        CalendarQueryAcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, CalendarDescriptor>(StringComparer.Ordinal);
        foreach (var selection in selections.Distinct()
                     .OrderBy(selection => selection.Calendar.Href, StringComparer.Ordinal)
                     .ThenBy(selection => selection.Kind))
        {
            var hrefs = await transport.QueryCandidateHrefsAsync(
                selection.Calendar.Href,
                selection.Kind,
                request.From,
                request.To,
                cancellationToken).ConfigureAwait(false);
            foreach (var href in hrefs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCanonicalDirectCandidate(selection.Calendar.Href, href))
                    throw new CalendarDiscoveryProtocolException("Unsafe Calendar Object Resource candidate href.");
                if (!candidates.TryAdd(href, selection.Calendar)
                    && !string.Equals(candidates[href].Href, selection.Calendar.Href, StringComparison.Ordinal))
                    throw new CalendarDiscoveryProtocolException("Calendar Object Resource candidate has conflicting Calendar identities.");
                if (candidates.Count > MaximumResources)
                    return null;
            }
        }
        CalendarQueryTelemetry.Add("caldav.query.candidate_count", candidates.Count);
        return candidates;
    }

    private static SelectionResult Select(
        CalendarQueryAcquisitionRequest request,
        CalendarQueryDiscovery discovery) => request.Scope.Mode switch
        {
            CalendarEntityScopeMode.Default => SelectDefaults(request.EntityKinds, discovery),
            CalendarEntityScopeMode.Selected => SelectExplicit(request, discovery.Discovery.Items),
            CalendarEntityScopeMode.All => SelectionResult.Success(discovery.Discovery.Items
                .SelectMany(calendar => request.EntityKinds
                    .Where(kind => Supports(calendar, kind))
                    .Select(kind => (calendar, kind))).ToArray()),
            _ => SelectionResult.Failure(CalendarQueryFailures.InvalidInput())
        };

    private static SelectionResult SelectDefaults(
        IReadOnlyList<CalendarEntityKind> kinds,
        CalendarQueryDiscovery discovery)
    {
        var selected = new List<(CalendarDescriptor Calendar, CalendarEntityKind Kind)>();
        foreach (var kind in kinds)
        {
            var result = discovery.Default(kind);
            if (result.Code != CalendarSelectionCode.Success)
                return SelectionResult.Failure(SelectionFailure(result));
            selected.Add((result.Calendar!, kind));
        }
        return SelectionResult.Success(selected);
    }

    private static SelectionResult SelectExplicit(
        CalendarQueryAcquisitionRequest request,
        IReadOnlyList<CalendarDescriptor> scoped)
    {
        var matches = scoped.Where(calendar => request.Scope.Calendar!.Name is not null
                ? string.Equals(calendar.DisplayName?.Trim(), request.Scope.Calendar.Name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(calendar.Href, request.Scope.Calendar.Href, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            return SelectionResult.Failure(CalendarQueryFailures.NotFound(scoped));
        if (matches.Length > 1)
            return SelectionResult.Failure(CalendarQueryFailures.Ambiguous(matches));
        var diagnostics = request.EntityKinds.Any(kind => !Supports(matches[0], kind))
            ? new[] { new QueryDiagnostic(
                "entity_kind_not_advertised",
                "The selected Calendar does not advertise one requested Entity Kind.",
                "warning") }
            : [];
        return SelectionResult.Success(request.EntityKinds.Select(kind => (matches[0], kind)).ToArray(), diagnostics);
    }

    private CalendarQueryDiscovery ValidateDiscovery(CalendarQueryDiscovery discovery)
    {
        var items = discovery.Discovery.Items;
        if (items.Count > 256
            || items.Select(calendar => calendar.Href).Distinct(StringComparer.Ordinal).Count() != items.Count
            || items.Any(calendar => !IsSafeCalendarHref(calendar.Href)
                || !new Uri(calendar.Href, UriKind.Absolute).AbsolutePath.EndsWith('/')))
            throw new CalendarDiscoveryProtocolException("The scoped Calendar discovery result is invalid.");
        var frozen = items.OrderBy(calendar => calendar.Href, StringComparer.Ordinal).Select(Freeze).ToArray();
        var byHref = frozen.ToDictionary(calendar => calendar.Href, StringComparer.Ordinal);
        return new CalendarQueryDiscovery(
            new CalendarDiscoveryResult(
                frozen,
                discovery.Discovery.Diagnostics.Select(FreezeDiagnostic).ToArray()),
            FreezeSelection(discovery.EventDefault, byHref),
            FreezeSelection(discovery.TodoDefault, byHref));
    }

    private static CalendarSelectionResult FreezeSelection(
        CalendarSelectionResult selection,
        IReadOnlyDictionary<string, CalendarDescriptor> scoped)
    {
        var candidates = selection.Candidates.Select(candidate => candidate.Href)
            .Distinct(StringComparer.Ordinal)
            .Select(href => scoped.TryGetValue(href, out var candidate)
                ? candidate
                : throw new CalendarDiscoveryProtocolException("Default selection escaped Calendar Scope."))
            .ToArray();
        if (selection.Code != CalendarSelectionCode.Success)
            return CalendarSelectionResult.Failure(selection.Code, candidates);
        if (selection.Calendar is null || !scoped.TryGetValue(selection.Calendar.Href, out var calendar))
            throw new CalendarDiscoveryProtocolException("Default selection escaped Calendar Scope.");
        return CalendarSelectionResult.Success(calendar);
    }

    private static QueryFailure? ReadFailure(CalendarResourceRead read) => read.Code switch
    {
        CalendarResourceReadCode.Success when HasStrongEntityTag(read.EntityTag) => null,
        CalendarResourceReadCode.Success => CalendarQueryFailures.ConcurrencyUnavailable(),
        CalendarResourceReadCode.ConcurrencyUnavailable => CalendarQueryFailures.ConcurrencyUnavailable(),
        CalendarResourceReadCode.PayloadTooLarge => CalendarQueryFailures.PayloadTooLarge(
            "A Calendar Object Resource exceeds the safe payload limit.", read.ObservedByteCount),
        CalendarResourceReadCode.UnsupportedCapability => CalendarQueryFailures.UnsupportedCapability(),
        _ => CalendarQueryFailures.Protocol()
    };

    private static QueryFailure SelectionFailure(CalendarSelectionResult selection) => selection.Code switch
    {
        CalendarSelectionCode.NotFound => CalendarQueryFailures.NotFound(selection.Candidates),
        CalendarSelectionCode.Ambiguous => CalendarQueryFailures.Ambiguous(selection.Candidates),
        CalendarSelectionCode.OutsideScope => CalendarQueryFailures.OutsideScope(selection.Candidates),
        CalendarSelectionCode.UnsupportedCapability => CalendarQueryFailures.UnsupportedCapability(),
        _ => CalendarQueryFailures.Protocol()
    };

    private bool IsSafeCalendarHref(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var candidate))
            return false;
        return HasSafeCalendarShape(candidate, href)
            && HasSameOrigin(new Uri(_options.BaseUrl, UriKind.Absolute), candidate);
    }

    private static bool IsValid(CalendarQueryAcquisitionRequest request) => request.EntityKinds.Count is >= 1 and <= 2
        && request.EntityKinds.All(kind => kind is CalendarEntityKind.Event or CalendarEntityKind.Todo)
        && request.EntityKinds.Distinct().Count() == request.EntityKinds.Count
        && HasValidWindow(request.From, request.To)
        && request.Scope.Mode switch
        {
            CalendarEntityScopeMode.Default or CalendarEntityScopeMode.All => request.Scope.Calendar is null,
            CalendarEntityScopeMode.Selected => HasOneSelector(request.Scope.Calendar),
            _ => false
        };

    private static bool IsCanonicalDirectCandidate(string calendarHref, string resourceHref)
    {
        if (!Uri.TryCreate(calendarHref, UriKind.Absolute, out var calendar)
            || !Uri.TryCreate(resourceHref, UriKind.Absolute, out var resource)
            || !HasSafeResourceShape(resource, resourceHref)
            || !HasSameOrigin(calendar, resource))
            return false;
        var calendarPath = calendar.AbsolutePath.EndsWith('/') ? calendar.AbsolutePath : calendar.AbsolutePath + '/';
        if (!resource.AbsolutePath.StartsWith(calendarPath, StringComparison.Ordinal))
            return false;
        var relative = resource.AbsolutePath[calendarPath.Length..];
        return relative.Length > 0 && !relative.Contains('/');
    }

    private static bool HasSafeResourceShape(Uri resource, string href) => HasSafeCalendarShape(resource, href)
        && !href.Contains("%2e", StringComparison.OrdinalIgnoreCase);

    private static bool HasSafeCalendarShape(Uri candidate, string href) => candidate.Scheme is "http" or "https"
        && string.IsNullOrEmpty(candidate.UserInfo)
        && string.IsNullOrEmpty(candidate.Query)
        && string.IsNullOrEmpty(candidate.Fragment)
        && !candidate.AbsolutePath.Contains("%2F", StringComparison.OrdinalIgnoreCase)
        && !candidate.AbsolutePath.Contains("%5C", StringComparison.OrdinalIgnoreCase)
        && !href.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        && !href.Contains('\\')
        && string.Equals(candidate.AbsoluteUri, href, StringComparison.Ordinal);

    private static bool HasSameOrigin(Uri origin, Uri candidate) =>
        string.Equals(origin.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(origin.Host, candidate.Host, StringComparison.OrdinalIgnoreCase)
        && origin.Port == candidate.Port;

    private static bool HasValidWindow(DateTimeOffset? from, DateTimeOffset? to) => from is null || to is null
        ? from is null && to is null
        : from.Value.Offset == TimeSpan.Zero && to.Value.Offset == TimeSpan.Zero
            && to > from && to - from <= TimeSpan.FromDays(366);

    private static bool HasOneSelector(CalendarReference? reference)
    {
        if (reference is null)
            return false;
        var name = !string.IsNullOrWhiteSpace(reference.Name);
        var href = !string.IsNullOrWhiteSpace(reference.Href);
        return name != href && (!name || string.Equals(reference.Name, reference.Name!.Trim(), StringComparison.Ordinal));
    }

    private static bool Supports(CalendarDescriptor calendar, CalendarEntityKind kind) => kind == CalendarEntityKind.Event
        ? calendar.EventSupport != EntityKindSupport.NotAdvertised
        : calendar.TodoSupport != EntityKindSupport.NotAdvertised;

    private static bool HasStrongEntityTag(string? value) => value is not null
        && EntityTagHeaderValue.TryParse(value, out var entityTag) && !entityTag.IsWeak;

    private static IReadOnlyList<string> ParseScope(string? value) => value is null
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static CalendarDescriptor Freeze(CalendarDescriptor calendar) => calendar with
    {
        EventEvidence = calendar.EventEvidence.ToArray(),
        TodoEvidence = calendar.TodoEvidence.ToArray(),
        UnavailableProperties = calendar.UnavailableProperties.ToArray()
    };

    private static CalendarDiagnostic FreezeDiagnostic(CalendarDiagnostic diagnostic) => diagnostic.Code switch
    {
        "duplicate_calendar_href" => new CalendarDiagnostic(
            diagnostic.Code, "A Calendar href is configured more than once.", CalendarDiagnosticSeverity.Warning),
        "calendar_href_not_found" => new CalendarDiagnostic(
            diagnostic.Code, "A configured Calendar href was not discovered.", CalendarDiagnosticSeverity.Warning),
        _ => throw new CalendarDiscoveryProtocolException("The scoped Calendar discovery diagnostic is invalid.")
    };

    private static QueryDiagnostic ToQueryDiagnostic(CalendarDiagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.Severity switch
        {
            CalendarDiagnosticSeverity.Info => "info",
            CalendarDiagnosticSeverity.Warning => "warning",
            _ => "error"
        });

    private static void AddDiagnostic(ICollection<QueryDiagnostic> diagnostics)
    {
        if (diagnostics.Count < MaximumDiagnostics)
        {
            diagnostics.Add(new QueryDiagnostic(
                "resource_disappeared_during_query",
                "A REPORT candidate disappeared before its authoritative snapshot was read.",
                "warning"));
        }
    }

    private sealed record SelectionResult(
        IReadOnlyList<(CalendarDescriptor Calendar, CalendarEntityKind Kind)> Selections,
        IReadOnlyList<QueryDiagnostic> Diagnostics,
        QueryFailure? Error)
    {
        internal static SelectionResult Success(
            IReadOnlyList<(CalendarDescriptor Calendar, CalendarEntityKind Kind)> selections,
            IReadOnlyList<QueryDiagnostic>? diagnostics = null) => new(selections, diagnostics ?? [], null);

        internal static SelectionResult Failure(QueryFailure error) => new([], [], error);
    }

    private sealed record FetchResult(
        IReadOnlyList<AcquiredCalendarResource> Resources,
        List<QueryDiagnostic> Diagnostics,
        QueryFailure? Error)
    {
        internal static FetchResult Success(
            IReadOnlyList<AcquiredCalendarResource> resources,
            List<QueryDiagnostic> diagnostics) => new(resources, diagnostics, null);

        internal static FetchResult Failure(QueryFailure error) => new([], [], error);
    }
}

internal sealed record CalendarTemporalContextRequest(
    bool IsRequired,
    string? EvaluationTimeZone,
    string QueryName);

internal sealed record CalendarTemporalContextResolution(
    TemporalEvaluationContext? Context,
    QueryFailure? Error)
{
    internal static CalendarTemporalContextResolution Success(TemporalEvaluationContext? context) => new(context, null);

    internal static CalendarTemporalContextResolution Failure(QueryFailure error) => new(null, error);
}

internal sealed class CalendarTemporalContextResolver(IOptions<CalDavOptions> options)
{
    internal CalendarTemporalContextResolution Resolve(CalendarTemporalContextRequest request)
    {
        if (!request.IsRequired)
        {
            return request.EvaluationTimeZone is null
                ? CalendarTemporalContextResolution.Success(null)
                : CalendarTemporalContextResolution.Failure(CalendarQueryFailures.InvalidInput(
                    $"An unbounded {request.QueryName} query cannot use evaluationTimeZone."));
        }
        if (request.EvaluationTimeZone is { } caller)
        {
            return IanaTimeZoneIds.IsValid(caller)
                ? CalendarTemporalContextResolution.Success(new TemporalEvaluationContext(
                    caller, TemporalEvaluationContextSource.Caller))
                : CalendarTemporalContextResolution.Failure(CalendarQueryFailures.InvalidInput(
                    $"The {request.QueryName} query evaluationTimeZone is invalid."));
        }
        return options.Value.EvaluationTimeZone is { } configured && IanaTimeZoneIds.IsValid(configured)
            ? CalendarTemporalContextResolution.Success(new TemporalEvaluationContext(
                configured, TemporalEvaluationContextSource.Configuration))
            : CalendarTemporalContextResolution.Failure(CalendarQueryFailures.InvalidInput(
                $"A bounded {request.QueryName} query requires a Temporal Evaluation Context."));
    }
}
