using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarQueryModule(
    CalendarEntityQueryStartExecutor startExecutor,
    CalendarEntityQueryContinueExecutor continueExecutor) : ICalendarQueryModule
{
    public async Task<QueryReply<CalendarEntityQueryItem>> QueryEntitiesAsync(
        CalendarEntityQueryRequest request,
        CancellationToken cancellationToken)
    {
        CalendarQueryTelemetry.Begin(request is CalendarEntityQueryRequest.Continue);
        return request switch
        {
            CalendarEntityQueryRequest.Start start => await startExecutor.ExecuteAsync(start, cancellationToken)
                .ConfigureAwait(false),
            CalendarEntityQueryRequest.Continue continuation => await continueExecutor.ExecuteAsync(
                    continuation,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => new QueryReply<CalendarEntityQueryItem>.Failure(CalendarQueryFailures.InvalidInput())
        };
    }
}

internal sealed class CalendarEntityQueryStartExecutor
{
    internal const int MaximumSnapshotItems = CalendarQuerySnapshotPolicy.MaximumItems;
    internal const long MaximumSnapshotBytes = CalendarQuerySnapshotPolicy.MaximumBytes;
    private const int MaximumDiagnostics = 32;
    private const int MaximumOccurrences = 5000;
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(10);
    private readonly Func<ICalendarQueryTransport> _transportFactory;
    private readonly CalDavOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CalendarQuerySnapshotWriter _snapshotWriter;
    private readonly CalendarEntityQueryPageCodec _pageCodec;
    private readonly CalendarQueryResourceRetriever _resourceRetriever;

    internal CalendarEntityQueryStartExecutor(
        Func<ICalendarQueryTransport> transportFactory,
        IOptions<CalDavOptions> options,
        TimeProvider timeProvider,
        CalendarQuerySnapshotWriter snapshotWriter,
        CalendarEntityQueryPageCodec pageCodec,
        CalendarQueryResourceRetriever resourceRetriever)
    {
        _transportFactory = transportFactory;
        _options = options.Value;
        _timeProvider = timeProvider;
        _snapshotWriter = snapshotWriter;
        _pageCodec = pageCodec;
        _resourceRetriever = resourceRetriever;
    }

    internal async Task<QueryReply<CalendarEntityQueryItem>> ExecuteAsync(
        CalendarEntityQueryRequest.Start request,
        CancellationToken cancellationToken)
    {
        if (!IsValid(request))
            return Failure(CalendarQueryFailures.InvalidInput());
        var temporal = ResolveTemporalContext(request.Query);
        if (temporal.Error is not null)
            return Failure(temporal.Error);
        var selectedHrefFailure = PrevalidateSelectedHref(request.Query);
        if (selectedHrefFailure is not null)
            return Failure(selectedHrefFailure);
        var transport = _transportFactory();
        var startedAt = _timeProvider.GetUtcNow();
        using var deadline = new CancellationTokenSource(ExecutionTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var completed = await CompleteQueryAsync(transport, request.Query, temporal.Context, linked.Token)
                .ConfigureAwait(false);
            ThrowIfDeadlineExpired(startedAt, linked.Token);
            if (completed.Error is not null)
                return Failure(completed.Error);
            return PublishFirstPage(completed, request.PageSize, linked.Token);
        }
        catch (CalendarDiscoveryLimitException exception)
        {
            return Failure(CalendarQueryFailures.Limit(
                "The query exceeded the Calendar limit.",
                new QueryExecutionLimits(CalendarCount: exception.CalendarCount)));
        }
        catch (HttpRequestException exception)
        {
            return Failure(CalendarQueryFailures.FromHttp(exception.StatusCode));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarQueryFailures.ElapsedLimit());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarQueryFailures.UpstreamUnavailable());
        }
        catch (Exception exception) when (exception is TimeoutException or TimeoutRejectedException
                                           or BrokenCircuitException)
        {
            return Failure(CalendarQueryFailures.UpstreamUnavailable());
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return Failure(CalendarQueryFailures.Protocol());
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Failure(CalendarQueryFailures.UnsupportedCapability());
        }
        catch (CalendarEntityQueryDeadlineException)
        {
            return Failure(CalendarQueryFailures.ElapsedLimit());
        }
    }

    private async Task<CompletedCalendarEntityQuery> CompleteQueryAsync(
        ICalendarQueryTransport transport,
        CalendarEntityQuery query,
        TemporalEvaluationContext? temporalContext,
        CancellationToken cancellationToken)
    {
        CalendarQueryDiscovery discovery;
        using (CalendarQueryTelemetry.StartPhase("discovery"))
            discovery = await transport.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        discovery = ValidateDiscovery(discovery);
        var selection = Select(query, discovery);
        if (selection.Error is not null)
            return CompletedCalendarEntityQuery.Failure(selection.Error);
        var diagnostics = discovery.Discovery.Diagnostics.Select(ToQueryDiagnostic)
            .Concat(selection.Diagnostics)
            .Take(MaximumDiagnostics)
            .ToList();
        IReadOnlyDictionary<string, CalendarDescriptor>? candidates;
        using (CalendarQueryTelemetry.StartPhase("candidate"))
        {
            candidates = await CollectCandidatesAsync(transport, selection.Selections, query, cancellationToken)
                .ConfigureAwait(false);
        }
        if (candidates is null)
        {
            return CompletedCalendarEntityQuery.Failure(CalendarQueryFailures.Limit(
                "The Calendar Entity query exhausted its resource budget.",
                new QueryExecutionLimits(ResourcesInspected: MaximumSnapshotItems + 1)));
        }
        FetchResult fetched;
        using (CalendarQueryTelemetry.StartPhase("fetch"))
            fetched = await FetchSnapshotsAsync(transport, candidates, diagnostics, cancellationToken).ConfigureAwait(false);
        if (fetched.Error is not null)
            return CompletedCalendarEntityQuery.Failure(fetched.Error);
        FilterResult filtered;
        using (CalendarQueryTelemetry.StartPhase("evaluation"))
            filtered = Filter(fetched.Snapshots, query, temporalContext, cancellationToken);
        if (filtered.Error is not null)
            return CompletedCalendarEntityQuery.Failure(filtered.Error);
        using (CalendarQueryTelemetry.StartPhase("serialization"))
            return Project(filtered.Snapshots, fetched.Diagnostics, temporalContext, cancellationToken);
    }

    private QueryReply<CalendarEntityQueryItem> PublishFirstPage(
        CompletedCalendarEntityQuery completed,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var firstPageAt = _timeProvider.GetUtcNow();
        var snapshot = new CalendarQuerySnapshot(
            Guid.NewGuid(),
            firstPageAt.Add(SnapshotLifetime),
            completed.Items,
            completed.DiagnosticsUtf8,
            completed.RetainedBytes,
            completed.TemporalEvaluationContextUtf8);
        CalendarQueryPagePlanAdmission planned;
        using (CalendarQueryTelemetry.StartPhase("page_admission"))
        {
            planned = _pageCodec.Plan(snapshot, 0, pageSize, cancellationToken);
            CalendarQueryTelemetry.Add("caldav.query.page_admission_count");
            if (planned.Error is not null)
                return Failure(planned.Error);
            if (planned.Value!.NextCursor is null)
            {
                var completePage = _pageCodec.Materialize(snapshot, planned.Value);
                return new QueryReply<CalendarEntityQueryItem>.Page(completePage);
            }
        }
        using (CalendarQueryTelemetry.StartPhase("reservation"))
        {
            var reservation = _snapshotWriter.TryReserve(snapshot);
            if (!reservation.IsAccepted)
                return Failure(CalendarQueryFailures.Busy(reservation.RetryAfterMs!.Value));
            using var lease = reservation.Lease!;
            cancellationToken.ThrowIfCancellationRequested();
            var page = _pageCodec.Materialize(snapshot, planned.Value);
            cancellationToken.ThrowIfCancellationRequested();
            QueryReply<CalendarEntityQueryItem> reply = new QueryReply<CalendarEntityQueryItem>.Page(page);
            if (!lease.Commit())
                return Failure(CalendarQueryFailures.UpstreamUnavailable());
            return reply;
        }
    }

    private static CompletedCalendarEntityQuery Project(
        IReadOnlyList<CalendarResourceSnapshot> snapshots,
        IReadOnlyList<QueryDiagnostic> diagnostics,
        TemporalEvaluationContext? temporalContext,
        CancellationToken cancellationToken)
    {
        var countFailure = CalendarQuerySnapshotPolicy.Validate(snapshots.Count, 0);
        if (countFailure is not null)
            return CompletedCalendarEntityQuery.Failure(countFailure);
        var projected = ImmutableArray.CreateBuilder<StoredCalendarEntityQueryItem>(snapshots.Count);
        long itemBytes = 0;
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = CalendarEntityQueryProjector.Project(snapshot);
            CalendarQueryTelemetry.Add("caldav.query.serialization_count");
            projected.Add(item);
            itemBytes += item.JsonByteCount;
            var byteFailure = CalendarQuerySnapshotPolicy.Validate(projected.Count, itemBytes);
            if (byteFailure is not null)
                return CompletedCalendarEntityQuery.Failure(byteFailure);
        }
        var diagnosticsUtf8 = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(diagnostics);
        var temporalContextUtf8 = CalendarTemporalEvaluationContextCodec.Encode(temporalContext);
        var retainedBytes = itemBytes + diagnosticsUtf8.Length + temporalContextUtf8.Length;
        var retainedFailure = CalendarQuerySnapshotPolicy.Validate(projected.Count, retainedBytes);
        if (retainedFailure is not null)
            return CompletedCalendarEntityQuery.Failure(retainedFailure);
        return CompletedCalendarEntityQuery.Success(
            projected.MoveToImmutable(),
            diagnosticsUtf8,
            retainedBytes,
            temporalContextUtf8);
    }

    private async Task<FetchResult> FetchSnapshotsAsync(
        ICalendarQueryTransport transport,
        IReadOnlyDictionary<string, CalendarDescriptor> candidates,
        List<QueryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var retrieval = await _resourceRetriever.RetrieveAsync(transport, candidates, cancellationToken)
            .ConfigureAwait(false);
        if (retrieval.Error is not null)
            return FetchResult.Failure(retrieval.Error);
        var snapshots = new List<CalendarResourceSnapshot>();
        foreach (var group in retrieval.Resources.GroupBy(resource => resource.Calendar.Href, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var calendar = group.First().Calendar;
            var ordered = group.OrderBy(resource => resource.RequestedHref, StringComparer.Ordinal)
                .ToArray();
            var requested = ordered.Select(resource => resource.RequestedHref).ToArray();
            var reads = ordered.Select(resource => resource.Read).ToArray();
            var error = AccumulateBatch(calendar, requested, reads, snapshots, diagnostics);
            if (error is not null)
                return FetchResult.Failure(error);
        }
        return FetchResult.Success(snapshots, diagnostics);
    }

    private static QueryFailure? AccumulateBatch(
        CalendarDescriptor calendar,
        IReadOnlyList<string> requested,
        IReadOnlyList<CalendarResourceRead> reads,
        ICollection<CalendarResourceSnapshot> snapshots,
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
                AddDiagnostic(diagnostics, "resource_disappeared_during_query",
                    "A REPORT candidate disappeared before its authoritative snapshot was read.");
                continue;
            }
            var error = ReadFailure(read);
            if (error is not null)
                return error;
            snapshots.Add(CalendarResourceProjector.AttachSnapshot(calendar.Href, read).Snapshot!);
            CalendarQueryTelemetry.Add("caldav.query.snapshot_count");
        }
        return null;
    }

    private static QueryFailure? ReadFailure(CalendarResourceRead read) => read.Code switch
    {
        CalendarResourceReadCode.Success when HasStrongEntityTag(read.EntityTag) => null,
        CalendarResourceReadCode.ConcurrencyUnavailable => CalendarQueryFailures.ConcurrencyUnavailable(),
        CalendarResourceReadCode.PayloadTooLarge => CalendarQueryFailures.PayloadTooLarge(
            "A Calendar Object Resource exceeds the safe payload limit.",
            read.ObservedByteCount),
        CalendarResourceReadCode.UnsupportedCapability => CalendarQueryFailures.UnsupportedCapability(),
        _ => CalendarQueryFailures.Protocol()
    };

    private static bool HasStrongEntityTag(string? value) => value is not null
        && EntityTagHeaderValue.TryParse(value, out var entityTag)
        && !entityTag.IsWeak;

    private static FilterResult Filter(
        IReadOnlyList<CalendarResourceSnapshot> snapshots,
        CalendarEntityQuery query,
        TemporalEvaluationContext? temporalContext,
        CancellationToken cancellationToken)
    {
        var filtered = new List<CalendarResourceSnapshot>();
        var occurrenceCount = 0;
        foreach (var snapshot in snapshots.Where(snapshot => MatchesKind(snapshot, query.EntityKinds)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque)
            {
                filtered.Add(snapshot);
                continue;
            }
            CalendarQueryTelemetry.Add("caldav.query.evaluation_count");
            var temporal = CalendarEntityTemporalMatcher.Matches(
                snapshot,
                query.From,
                query.To,
                temporalContext?.TimeZone,
                cancellationToken);
            occurrenceCount += temporal.OccurrenceCount;
            var failure = TemporalFailure(temporal.Match, occurrenceCount);
            if (failure is not null)
                return FilterResult.Failure(failure);
            if (temporal.Match != CalendarEntityTemporalMatch.NoMatch)
                filtered.Add(snapshot);
        }
        return FilterResult.Success(filtered
            .OrderBy(snapshot => snapshot.CalendarHref, StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.ResourceHref, StringComparer.Ordinal)
            .ToArray());
    }

    private static QueryFailure? TemporalFailure(CalendarEntityTemporalMatch match, int occurrenceCount) => match switch
    {
        CalendarEntityTemporalMatch.LimitExhausted => CalendarQueryFailures.Limit(
            "The Calendar Entity query exhausted its occurrence budget.",
            new QueryExecutionLimits(OccurrenceCount: occurrenceCount)),
        _ when occurrenceCount > MaximumOccurrences => CalendarQueryFailures.Limit(
            "The Calendar Entity query exhausted its occurrence budget.",
            new QueryExecutionLimits(OccurrenceCount: occurrenceCount)),
        CalendarEntityTemporalMatch.Unresolved => CalendarQueryFailures.TemporalUnresolved(),
        CalendarEntityTemporalMatch.Unevaluable => CalendarQueryFailures.RecurrenceUnevaluable(),
        _ => null
    };

    private static async Task<IReadOnlyDictionary<string, CalendarDescriptor>?> CollectCandidatesAsync(
        ICalendarQueryTransport transport,
        IReadOnlyList<(CalendarDescriptor Calendar, CalendarEntityKind Kind)> selections,
        CalendarEntityQuery query,
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
                query.From,
                query.To,
                cancellationToken).ConfigureAwait(false);
            foreach (var href in hrefs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCanonicalDirectCandidate(selection.Calendar.Href, href))
                    throw new CalendarDiscoveryProtocolException("Unsafe Calendar Object Resource candidate href.");
                if (!candidates.TryAdd(href, selection.Calendar)
                    && !string.Equals(candidates[href].Href, selection.Calendar.Href, StringComparison.Ordinal))
                    throw new CalendarDiscoveryProtocolException("Calendar Object Resource candidate has conflicting Calendar identities.");
                if (candidates.Count > MaximumSnapshotItems)
                    return null;
            }
        }
        CalendarQueryTelemetry.Add("caldav.query.candidate_count", candidates.Count);
        return candidates;
    }

    private static bool IsCanonicalDirectCandidate(string calendarHref, string resourceHref)
    {
        if (!Uri.TryCreate(calendarHref, UriKind.Absolute, out var calendar)
            || !Uri.TryCreate(resourceHref, UriKind.Absolute, out var resource)
            || !HasCanonicalResourceShape(resource, resourceHref)
            || !HasSameOrigin(calendar, resource))
            return false;
        return IsDirectChild(calendar, resource);
    }

    private static bool HasCanonicalResourceShape(Uri resource, string resourceHref) =>
        resource.Scheme is "http" or "https"
        && string.IsNullOrEmpty(resource.UserInfo)
        && string.IsNullOrEmpty(resource.Query)
        && string.IsNullOrEmpty(resource.Fragment)
        && !resourceHref.Contains('\\')
        && !resourceHref.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        && !HasEncodedSeparator(resource)
        && string.Equals(resource.AbsoluteUri, resourceHref, StringComparison.Ordinal);

    private static bool IsDirectChild(Uri calendar, Uri resource)
    {
        var calendarPath = calendar.AbsolutePath.EndsWith('/') ? calendar.AbsolutePath : calendar.AbsolutePath + '/';
        if (!resource.AbsolutePath.StartsWith(calendarPath, StringComparison.Ordinal))
            return false;
        var relative = resource.AbsolutePath[calendarPath.Length..];
        return relative.Length > 0 && !relative.Contains('/');
    }

    private static SelectionResult Select(
        CalendarEntityQuery query,
        CalendarQueryDiscovery discovery) => query.Scope.Mode switch
        {
            CalendarEntityScopeMode.Default => SelectDefaults(query.EntityKinds, discovery),
            CalendarEntityScopeMode.Selected => SelectExplicit(query, discovery.Discovery.Items),
            CalendarEntityScopeMode.All => SelectionResult.Success(discovery.Discovery.Items.SelectMany(calendar => query.EntityKinds
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

    private static SelectionResult SelectExplicit(CalendarEntityQuery query, IReadOnlyList<CalendarDescriptor> scoped)
    {
        var matches = scoped.Where(calendar => query.Scope.Calendar!.Name is not null
                ? string.Equals(calendar.DisplayName?.Trim(), query.Scope.Calendar.Name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(calendar.Href, query.Scope.Calendar.Href, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            return SelectionResult.Failure(CalendarQueryFailures.NotFound(scoped));
        if (matches.Length > 1)
            return SelectionResult.Failure(CalendarQueryFailures.Ambiguous(matches));
        var diagnostics = query.EntityKinds.Any(kind => !Supports(matches[0], kind))
            ? new[] { new QueryDiagnostic(
                "entity_kind_not_advertised",
                "The selected Calendar does not advertise one requested Entity Kind.",
                "warning") }
            : [];
        return SelectionResult.Success(query.EntityKinds.Select(kind => (matches[0], kind)).ToArray(), diagnostics);
    }

    private static QueryFailure SelectionFailure(CalendarSelectionResult selection) => selection.Code switch
    {
        CalendarSelectionCode.NotFound => CalendarQueryFailures.NotFound(selection.Candidates),
        CalendarSelectionCode.Ambiguous => CalendarQueryFailures.Ambiguous(selection.Candidates),
        CalendarSelectionCode.OutsideScope => CalendarQueryFailures.OutsideScope(selection.Candidates),
        CalendarSelectionCode.UnsupportedCapability => CalendarQueryFailures.UnsupportedCapability(),
        _ => CalendarQueryFailures.Protocol()
    };

    private QueryFailure? PrevalidateSelectedHref(CalendarEntityQuery query)
    {
        var href = query.Scope.Mode == CalendarEntityScopeMode.Selected ? query.Scope.Calendar?.Href : null;
        if (href is null)
            return null;
        if (!IsSafeCalendarHref(href))
            return CalendarQueryFailures.UnsafeHref();
        var scope = ParseScope(_options.CalendarHrefs);
        return scope.Count > 0 && !scope.Contains(href, StringComparer.Ordinal)
            ? CalendarQueryFailures.OutsideScope([])
            : null;
    }

    private bool IsSafeCalendarHref(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var candidate))
            return false;
        var origin = new Uri(_options.BaseUrl, UriKind.Absolute);
        return HasSafeCalendarShape(candidate, href) && HasSameOrigin(origin, candidate);
    }

    private CalendarQueryDiscovery ValidateDiscovery(CalendarQueryDiscovery discovery)
    {
        var items = discovery.Discovery.Items;
        if (items.Count > 256
            || items.Select(calendar => calendar.Href).Distinct(StringComparer.Ordinal).Count() != items.Count
            || items.Any(calendar => !IsSafeCalendarHref(calendar.Href)
                || !new Uri(calendar.Href, UriKind.Absolute).AbsolutePath.EndsWith('/')))
            throw new CalendarDiscoveryProtocolException("The scoped Calendar discovery result is invalid.");
        var frozen = items.OrderBy(calendar => calendar.Href, StringComparer.Ordinal)
            .Select(Freeze)
            .ToArray();
        var byHref = frozen.ToDictionary(calendar => calendar.Href, StringComparer.Ordinal);
        var diagnostics = discovery.Discovery.Diagnostics.Select(FreezeDiagnostic).ToArray();
        return new CalendarQueryDiscovery(
            new CalendarDiscoveryResult(frozen, diagnostics),
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

    private static CalendarDiagnostic FreezeDiagnostic(CalendarDiagnostic diagnostic) => diagnostic.Code switch
    {
        "duplicate_calendar_href" => new CalendarDiagnostic(
            diagnostic.Code,
            "A Calendar href is configured more than once.",
            CalendarDiagnosticSeverity.Warning),
        "calendar_href_not_found" => new CalendarDiagnostic(
            diagnostic.Code,
            "A configured Calendar href was not discovered.",
            CalendarDiagnosticSeverity.Warning),
        _ => throw new CalendarDiscoveryProtocolException("The scoped Calendar discovery diagnostic is invalid.")
    };

    private static CalendarDescriptor Freeze(CalendarDescriptor calendar) => calendar with
    {
        EventEvidence = calendar.EventEvidence.ToArray(),
        TodoEvidence = calendar.TodoEvidence.ToArray(),
        UnavailableProperties = calendar.UnavailableProperties.ToArray()
    };

    private static bool HasSafeCalendarShape(Uri candidate, string href) => candidate.Scheme is "http" or "https"
        && string.IsNullOrEmpty(candidate.UserInfo)
        && string.IsNullOrEmpty(candidate.Query)
        && string.IsNullOrEmpty(candidate.Fragment)
        && !HasEncodedSeparator(candidate)
        && !href.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        && !href.Contains('\\')
        && string.Equals(candidate.AbsoluteUri, href, StringComparison.Ordinal);

    private static bool HasEncodedSeparator(Uri candidate) =>
        candidate.AbsolutePath.Contains("%2F", StringComparison.OrdinalIgnoreCase)
        || candidate.AbsolutePath.Contains("%5C", StringComparison.OrdinalIgnoreCase);

    private static bool HasSameOrigin(Uri origin, Uri candidate) =>
        string.Equals(origin.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(origin.Host, candidate.Host, StringComparison.OrdinalIgnoreCase)
        && origin.Port == candidate.Port;

    private void ThrowIfDeadlineExpired(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_timeProvider.GetUtcNow() >= startedAt.Add(ExecutionTimeout))
            throw new CalendarEntityQueryDeadlineException();
    }

    private static bool IsValid(CalendarEntityQueryRequest.Start request) =>
        request.Query is not null
        && request.PageSize is >= 1 and <= CalendarEntityQueryPageCodec.MaximumPageSize
        && IsValidQuery(request.Query);

    private static bool IsValidQuery(CalendarEntityQuery query) => query.EntityKinds.Count is >= 1 and <= 2
        && query.EntityKinds.All(kind => kind is CalendarEntityKind.Event or CalendarEntityKind.Todo)
        && query.EntityKinds.Distinct().Count() == query.EntityKinds.Count
        && HasValidWindow(query.From, query.To)
        && query.Scope.Mode switch
        {
            CalendarEntityScopeMode.Default or CalendarEntityScopeMode.All => query.Scope.Calendar is null,
            CalendarEntityScopeMode.Selected => HasOneSelector(query.Scope.Calendar),
            _ => false
        };

    private TemporalContextResolution ResolveTemporalContext(CalendarEntityQuery query)
    {
        var isBounded = query.From is not null;
        if (!isBounded)
        {
            return query.EvaluationTimeZone is null
                ? TemporalContextResolution.Success(null)
                : TemporalContextResolution.Failure(CalendarQueryFailures.InvalidInput(
                    "An unbounded Calendar Entity query cannot use evaluationTimeZone."));
        }
        if (query.EvaluationTimeZone is { } caller)
        {
            return IanaTimeZoneIds.IsValid(caller)
                ? TemporalContextResolution.Success(new TemporalEvaluationContext(
                    caller,
                    TemporalEvaluationContextSource.Caller))
                : TemporalContextResolution.Failure(CalendarQueryFailures.InvalidInput(
                    "The Calendar Entity query evaluationTimeZone is invalid."));
        }
        return _options.EvaluationTimeZone is { } configured && IanaTimeZoneIds.IsValid(configured)
            ? TemporalContextResolution.Success(new TemporalEvaluationContext(
                configured,
                TemporalEvaluationContextSource.Configuration))
            : TemporalContextResolution.Failure(CalendarQueryFailures.InvalidInput(
                "A bounded Calendar Entity query requires a Temporal Evaluation Context."));
    }

    private static bool HasValidWindow(DateTimeOffset? from, DateTimeOffset? to) => from is null || to is null
        ? from is null && to is null
        : from.Value.Offset == TimeSpan.Zero
            && to.Value.Offset == TimeSpan.Zero
            && to > from
            && to - from <= TimeSpan.FromDays(366);

    private static bool HasOneSelector(CalendarReference? reference)
    {
        if (reference is null)
            return false;
        var name = !string.IsNullOrWhiteSpace(reference.Name);
        var href = !string.IsNullOrWhiteSpace(reference.Href);
        return name != href && (!name || string.Equals(reference.Name, reference.Name!.Trim(), StringComparison.Ordinal));
    }

    private static bool MatchesKind(CalendarResourceSnapshot snapshot, IReadOnlyList<CalendarEntityKind> kinds) =>
        snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque
        || kinds.Any(kind => kind == CalendarEntityKind.Event
            ? snapshot.Projection.Kind == CalendarResourceProjectionKind.Event
            : snapshot.Projection.Kind == CalendarResourceProjectionKind.Todo);

    private static bool Supports(CalendarDescriptor calendar, CalendarEntityKind kind) => kind == CalendarEntityKind.Event
        ? calendar.EventSupport != EntityKindSupport.NotAdvertised
        : calendar.TodoSupport != EntityKindSupport.NotAdvertised;

    private static IReadOnlyList<string> ParseScope(string? value) => value is null
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static QueryDiagnostic ToQueryDiagnostic(CalendarDiagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.Severity switch
        {
            CalendarDiagnosticSeverity.Info => "info",
            CalendarDiagnosticSeverity.Warning => "warning",
            _ => "error"
        });

    private static void AddDiagnostic(ICollection<QueryDiagnostic> diagnostics, string code, string message)
    {
        if (diagnostics.Count < MaximumDiagnostics)
            diagnostics.Add(new QueryDiagnostic(code, message, "warning"));
    }

    private static QueryReply<CalendarEntityQueryItem>.Failure Failure(QueryFailure failure) => new(failure);

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
        IReadOnlyList<CalendarResourceSnapshot> Snapshots,
        List<QueryDiagnostic> Diagnostics,
        QueryFailure? Error)
    {
        internal static FetchResult Success(
            IReadOnlyList<CalendarResourceSnapshot> snapshots,
            List<QueryDiagnostic> diagnostics) => new(snapshots, diagnostics, null);

        internal static FetchResult Failure(QueryFailure error) => new([], [], error);
    }

    private sealed record FilterResult(IReadOnlyList<CalendarResourceSnapshot> Snapshots, QueryFailure? Error)
    {
        internal static FilterResult Success(IReadOnlyList<CalendarResourceSnapshot> snapshots) => new(snapshots, null);

        internal static FilterResult Failure(QueryFailure error) => new([], error);
    }

    private sealed record TemporalContextResolution(
        TemporalEvaluationContext? Context,
        QueryFailure? Error)
    {
        internal static TemporalContextResolution Success(TemporalEvaluationContext? context) => new(context, null);

        internal static TemporalContextResolution Failure(QueryFailure error) => new(null, error);
    }

    private sealed class CalendarEntityQueryDeadlineException : Exception;
}

internal sealed class CalendarEntityQueryContinueExecutor(
    CalendarQueryCursorAuthenticator cursorAuthenticator,
    CalendarQuerySnapshotReader snapshotReader,
    CalendarEntityQueryPageCodec pageCodec)
{
    internal Task<QueryReply<CalendarEntityQueryItem>> ExecuteAsync(
        CalendarEntityQueryRequest.Continue request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pageSize = request.PageSize ?? CalendarEntityQueryPageCodec.DefaultPageSize;
        if (string.IsNullOrEmpty(request.Cursor)
            || pageSize is < 1 or > CalendarEntityQueryPageCodec.MaximumPageSize)
            return Task.FromResult<QueryReply<CalendarEntityQueryItem>>(Failure(CalendarQueryFailures.InvalidInput()));
        CalendarQueryCursor cursor;
        CalendarQuerySnapshot? snapshot;
        using (CalendarQueryTelemetry.StartPhase("snapshot_lookup"))
        {
            var authentication = cursorAuthenticator.Authenticate(request.Cursor, CalendarEntityQueryPageCodec.ToolName);
            if (authentication.Code == CalendarQueryCursorAuthenticationCode.Expired)
                return Task.FromResult<QueryReply<CalendarEntityQueryItem>>(Failure(CalendarQueryFailures.CursorExpired()));
            if (authentication.Code != CalendarQueryCursorAuthenticationCode.Valid)
                return Task.FromResult<QueryReply<CalendarEntityQueryItem>>(Failure(CalendarQueryFailures.InvalidCursor()));
            cursor = authentication.Cursor!;
            snapshot = snapshotReader.Get(cursor.SnapshotId);
            CalendarQueryTelemetry.Add("caldav.query.snapshot_lookup_count");
            if (!MatchesSnapshot(cursor, snapshot))
                return Task.FromResult<QueryReply<CalendarEntityQueryItem>>(Failure(CalendarQueryFailures.InvalidCursor()));
        }
        using var pagePhase = CalendarQueryTelemetry.StartPhase("page_admission");
        var admitted = pageCodec.Admit(snapshot!, cursor.Position, pageSize, cancellationToken);
        CalendarQueryTelemetry.Add("caldav.query.page_admission_count");
        return Task.FromResult(admitted.Error is null
            ? new QueryReply<CalendarEntityQueryItem>.Page(admitted.Value!) as QueryReply<CalendarEntityQueryItem>
            : Failure(admitted.Error));
    }

    private bool MatchesSnapshot(CalendarQueryCursor cursor, CalendarQuerySnapshot? snapshot) => snapshot is not null
        && cursor.ExpiresAtUnixMilliseconds == snapshot.ExpiresAt.ToUnixTimeMilliseconds()
        && cursor.Position > 0
        && cursor.Position < snapshot.Items.Length
        && cursorAuthenticator.MatchesTemporalContext(cursor, snapshot.TemporalEvaluationContextUtf8.Span);

    private static QueryReply<CalendarEntityQueryItem>.Failure Failure(QueryFailure failure) => new(failure);
}

internal sealed record CompletedCalendarEntityQuery(
    ImmutableArray<StoredCalendarEntityQueryItem> Items,
    ReadOnlyMemory<byte> DiagnosticsUtf8,
    long RetainedBytes,
    ReadOnlyMemory<byte> TemporalEvaluationContextUtf8,
    QueryFailure? Error)
{
    internal static CompletedCalendarEntityQuery Success(
        ImmutableArray<StoredCalendarEntityQueryItem> items,
        ReadOnlyMemory<byte> diagnosticsUtf8,
        long retainedBytes,
        ReadOnlyMemory<byte> temporalEvaluationContextUtf8) =>
        new(items, diagnosticsUtf8, retainedBytes, temporalEvaluationContextUtf8, null);

    internal static CompletedCalendarEntityQuery Failure(QueryFailure error) =>
        new([], ReadOnlyMemory<byte>.Empty, 0, ReadOnlyMemory<byte>.Empty, error);
}

internal static class CalendarQueryFailures
{
    internal static QueryFailure InvalidInput(string message = "The Calendar Entity query input is invalid.") =>
        new(QueryFailureCode.InvalidInput, QueryFailureCategory.Input, message, false,
            QueryFailurePhase.SchemaLexicalDiscriminator);

    internal static QueryFailure InvalidCursor() =>
        new(QueryFailureCode.InvalidInput, QueryFailureCategory.Input, "The continuation cursor is invalid.", false,
            QueryFailurePhase.Pagination);

    internal static QueryFailure UnsafeHref() => new(
        QueryFailureCode.InvalidInput,
        QueryFailureCategory.Input,
        "The Calendar Entity query Calendar href is unsafe.",
        false,
        QueryFailurePhase.OriginScopeAuthorization);

    internal static QueryFailure CursorExpired() => new(
        QueryFailureCode.CursorExpired,
        QueryFailureCategory.State,
        "The Query Result Snapshot expired; start a new cursorless query.",
        false,
        QueryFailurePhase.Pagination);

    internal static QueryFailure Limit(string message, QueryExecutionLimits? limits = null) =>
        new(QueryFailureCode.LimitExhausted, QueryFailureCategory.LimitsAndAdmission, message, false,
            QueryFailurePhase.Execution, limits);

    internal static QueryFailure ElapsedLimit() => Limit(
        "The Calendar Entity query exhausted the elapsed_time execution budget.",
        new QueryExecutionLimits(
            Dimension: QueryLimitDimension.ElapsedTime,
            Observed: 30_000,
            Limit: 30_000));

    internal static QueryFailure Busy(int retryAfterMs) => new(
        QueryFailureCode.Busy,
        QueryFailureCategory.LimitsAndAdmission,
        "Query Result Snapshot capacity is temporarily unavailable.",
        true,
        QueryFailurePhase.Pagination,
        RetryAfterMs: retryAfterMs);

    internal static QueryFailure PayloadTooLarge(string message, int? byteCount = null) => new(
        QueryFailureCode.PayloadTooLarge,
        QueryFailureCategory.LimitsAndAdmission,
        message,
        false,
        QueryFailurePhase.AdmissionAndPayload,
        byteCount is null ? null : new QueryExecutionLimits(ByteCount: byteCount));

    internal static QueryFailure Protocol() => new(
        QueryFailureCode.UpstreamProtocolError,
        QueryFailureCategory.Upstream,
        "The Calendar Entity query returned an invalid response.",
        false,
        QueryFailurePhase.Execution);

    internal static QueryFailure UnsupportedCapability() => new(
        QueryFailureCode.UnsupportedCapability,
        QueryFailureCategory.CapabilityAndProjection,
        "The server does not support the required Calendar query capability.",
        false,
        QueryFailurePhase.SelectionDiscoveryCapability);

    internal static QueryFailure ConcurrencyUnavailable() => new(
        QueryFailureCode.ConcurrencyUnavailable,
        QueryFailureCategory.State,
        "A query candidate did not provide a strong Entity Tag.",
        false,
        QueryFailurePhase.TargetRevision);

    internal static QueryFailure TemporalUnresolved() => new(
        QueryFailureCode.TemporalUnresolved,
        QueryFailureCategory.CapabilityAndProjection,
        "Temporal evaluation could not be resolved.",
        false,
        QueryFailurePhase.CompleteResourceSemantics);

    internal static QueryFailure RecurrenceUnevaluable() => new(
        QueryFailureCode.RecurrenceUnevaluable,
        QueryFailureCategory.CapabilityAndProjection,
        "The Recurrence Set could not be evaluated.",
        false,
        QueryFailurePhase.CompleteResourceSemantics);

    internal static QueryFailure UpstreamUnavailable() => new(
        QueryFailureCode.UpstreamUnavailable,
        QueryFailureCategory.Upstream,
        "The Calendar Entity query is temporarily unavailable.",
        true,
        QueryFailurePhase.Execution);

    internal static QueryFailure NotFound(IReadOnlyList<CalendarDescriptor> candidates) => new(
        QueryFailureCode.NotFound,
        QueryFailureCategory.Selection,
        "No matching authorized Calendar was found.",
        false,
        QueryFailurePhase.SelectionDiscoveryCapability,
        AuthorizedCandidates: Candidates(candidates));

    internal static QueryFailure Ambiguous(IReadOnlyList<CalendarDescriptor> candidates) => new(
        QueryFailureCode.Ambiguous,
        QueryFailureCategory.Selection,
        "The Calendar selector matched more than one authorized Calendar.",
        false,
        QueryFailurePhase.SelectionDiscoveryCapability,
        AuthorizedCandidates: Candidates(candidates));

    internal static QueryFailure OutsideScope(IReadOnlyList<CalendarDescriptor> candidates) => new(
        QueryFailureCode.OutsideScope,
        QueryFailureCategory.Selection,
        "The selected Calendar is outside the configured Calendar Scope.",
        false,
        QueryFailurePhase.OriginScopeAuthorization,
        AuthorizedCandidates: Candidates(candidates));

    internal static QueryFailure FromHttp(HttpStatusCode? statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => Upstream(QueryFailureCode.UpstreamUnauthorized,
            "The Calendar Entity query was not authorized."),
        HttpStatusCode.Forbidden => Upstream(QueryFailureCode.UpstreamForbidden,
            "The Calendar Entity query was forbidden."),
        HttpStatusCode.TooManyRequests => Upstream(
            QueryFailureCode.UpstreamRateLimited,
            "The Calendar Entity query is rate limited.",
            true),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => UnsupportedCapability(),
        HttpStatusCode.RequestEntityTooLarge => PayloadTooLarge("The Calendar Entity query response is too large."),
        HttpStatusCode.RequestTimeout or null => UpstreamUnavailable(),
        >= HttpStatusCode.InternalServerError => UpstreamUnavailable(),
        _ => Protocol()
    };

    private static QueryFailure Upstream(QueryFailureCode code, string message, bool retryable = false) =>
        new(code, QueryFailureCategory.Upstream, message, retryable, QueryFailurePhase.Execution);

    private static IReadOnlyList<QueryAuthorizedCandidate>? Candidates(IReadOnlyList<CalendarDescriptor> candidates) =>
        candidates.Count == 0
            ? null
            : candidates.Take(32).Select(calendar => new QueryAuthorizedCandidate(
                calendar.Href,
                calendar.DisplayName,
                calendar.EventSupport,
                calendar.TodoSupport,
                calendar.EventEvidence,
                calendar.TodoEvidence)).ToArray();
}
