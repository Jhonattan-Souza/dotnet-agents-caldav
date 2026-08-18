using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Owns bounded Calendar Scope planning, REPORT candidate collection, snapshot reads, and local filtering.</summary>
internal sealed class CalendarEntityQueryEngine
{
    private const int MaximumDiagnostics = 32;
    private const int MaximumQueryOccurrences = 5000;
    private const int MaximumQueryResources = 5000;
    private readonly ICalendarClient _calendarClient;
    private readonly CalDavOptions _options;
    private readonly Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> _applyScope;
    private readonly Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
        _resolveDefault;

    public CalendarEntityQueryEngine(
        ICalendarClient calendarClient,
        CalDavOptions options,
        Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> applyScope,
        Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult> resolveDefault)
    {
        _calendarClient = calendarClient;
        _options = options;
        _applyScope = applyScope;
        _resolveDefault = resolveDefault;
    }

    public async Task<CalendarEntityQueryResult> QueryAsync(
        CalendarEntityQuery query,
        CancellationToken cancellationToken,
        bool applyTemporalFilters = true)
    {
        if (!IsValidQueryShape(query))
            return CalendarEntityQueryResult.Failure(CalendarEntityQueryCode.InvalidInput);
        var prevalidation = PrevalidateSelectedHref(query);
        if (prevalidation is not null)
            return prevalidation;

        var discovered = await _calendarClient.GetCalendarsAsync(cancellationToken);
        var scopeResult = _applyScope(discovered);
        var scoped = scopeResult.Items;
        var plan = CreateSelectionPlan(query, discovered, scoped);
        if (plan.Code != CalendarEntityQueryCode.Success)
            return CalendarEntityQueryResult.Failure(plan.Code, plan.AuthorizedCandidates);

        plan = plan with
        {
            Diagnostics = scopeResult.Diagnostics.Select(ToResourceDiagnostic)
                .Concat(plan.Diagnostics)
                .Take(MaximumDiagnostics)
                .ToArray()
        };
        var candidates = await CollectCandidatesAsync(plan.Selections, query, cancellationToken);
        if (candidates is null)
            return CalendarEntityQueryResult.Failure(
                CalendarEntityQueryCode.LimitExhausted,
                limits: new CalendarEntityQueryExecutionLimits(ResourcesInspected: MaximumQueryResources + 1));
        var fetched = await FetchSnapshotsAsync(candidates, plan.Diagnostics, cancellationToken);
        if (fetched.Code != CalendarEntityQueryCode.Success)
            return CalendarEntityQueryResult.Failure(fetched.Code, limits: fetched.Limits);
        if (!applyTemporalFilters)
        {
            CalendarOperationProgress.SetPhase(CalendarOperationPhase.Filter);
            return CalendarEntityQueryResult.Success(fetched.Snapshots, fetched.Diagnostics);
        }

        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Filter);
        var filtered = ApplyFilters(fetched.Snapshots, query, fetched.Diagnostics, cancellationToken);
        return filtered.Code == CalendarEntityQueryCode.Success
            ? CalendarEntityQueryResult.Success(filtered.Snapshots, fetched.Diagnostics)
            : CalendarEntityQueryResult.Failure(
                filtered.Code,
                limits: filtered.Code == CalendarEntityQueryCode.LimitExhausted
                    && filtered.OccurrenceCount > 0
                    ? new CalendarEntityQueryExecutionLimits(OccurrenceCount: filtered.OccurrenceCount)
                    : null);
    }

    private async Task<IReadOnlyDictionary<string, CalendarDescriptor>?> CollectCandidatesAsync(
        IReadOnlyList<(CalendarDescriptor Calendar, CalendarEntityKind Kind)> selections,
        CalendarEntityQuery query,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, CalendarDescriptor>(StringComparer.Ordinal);
        foreach (var selection in selections.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hrefs = await _calendarClient.QueryCalendarResourceHrefsAsync(
                selection.Calendar.Href,
                selection.Kind,
                query.From,
                query.To,
                cancellationToken);
            foreach (var href in hrefs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates.TryAdd(href, selection.Calendar)
                    && candidates.Count > MaximumQueryResources)
                    return null;
            }
        }
        return candidates;
    }

    private async Task<CandidateFetchResult> FetchSnapshotsAsync(
        IReadOnlyDictionary<string, CalendarDescriptor> candidates,
        IReadOnlyList<CalendarResourceDiagnostic> initialDiagnostics,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<CalendarResourceSnapshot>();
        var snapshotKeys = new HashSet<(string CalendarHref, string ResourceHref)>();
        var diagnostics = initialDiagnostics.ToList();
        foreach (var calendarCandidates in candidates.GroupBy(candidate => candidate.Value.Href, StringComparer.Ordinal))
        {
            var calendar = calendarCandidates.First().Value;
            foreach (var batch in calendarCandidates.Select(candidate => candidate.Key)
                         .Chunk(CalendarQueryPolicy.MaximumMultigetBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<CalendarResourceRead> reads;
                try
                {
                    reads = await ReadBatchAsync(calendar, batch, cancellationToken);
                }
                catch (Exception exception) when (exception is CalendarDiscoveryProtocolException or XmlException)
                {
                    return CandidateFetchResult.Failure(CalendarEntityQueryCode.UpstreamProtocolError);
                }
                foreach (var read in reads)
                {
                    var failure = AccumulateRead(read, snapshots, snapshotKeys, diagnostics);
                    if (failure is not null)
                        return failure;
                }
            }
        }
        return CandidateFetchResult.Success(snapshots, diagnostics);
    }

    private async Task<IReadOnlyList<CalendarResourceRead>> ReadBatchAsync(
        CalendarDescriptor calendar,
        IReadOnlyList<string> hrefs,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CalendarResourceRead>? reads;
        try
        {
            reads = await _calendarClient.GetCalendarResourcesForQueryAsync(calendar.Href, hrefs, cancellationToken);
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            reads = null;
        }
        if (reads is null || reads.Count != hrefs.Count)
            reads = await ReadDirectlyAsync(hrefs, cancellationToken);
        return reads.Select(read => read.Code == CalendarResourceReadCode.Success
                ? CalendarResourceProjector.AttachSnapshot(calendar.Href, read)
                : read)
            .ToArray();
    }

    private async Task<IReadOnlyList<CalendarResourceRead>> ReadDirectlyAsync(
        IReadOnlyList<string> hrefs,
        CancellationToken cancellationToken)
    {
        var reads = new List<CalendarResourceRead>(hrefs.Count);
        foreach (var href in hrefs)
            reads.Add(await _calendarClient.GetCalendarResourceAsync(href, cancellationToken));
        return reads;
    }

    private static CandidateFetchResult? AccumulateRead(
        CalendarResourceRead read,
        ICollection<CalendarResourceSnapshot> snapshots,
        ISet<(string CalendarHref, string ResourceHref)> snapshotKeys,
        ICollection<CalendarResourceDiagnostic> diagnostics)
    {
        if (read.Code == CalendarResourceReadCode.NotFound)
        {
            AddDiagnostic(diagnostics, "resource_disappeared_during_query",
                "A REPORT candidate disappeared before its authoritative snapshot was read.");
            return null;
        }
        if (read.Code != CalendarResourceReadCode.Success || read.Snapshot is null)
        {
            return CandidateFetchResult.Failure(
                MapReadFailure(read.Code),
                read.ObservedByteCount is null
                    ? null
                    : new CalendarEntityQueryExecutionLimits(ByteCount: read.ObservedByteCount));
        }
        if (snapshotKeys.Add((read.Snapshot.CalendarHref, read.Snapshot.ResourceHref)))
            snapshots.Add(read.Snapshot);
        if (read.Snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque)
        {
            AddDiagnostic(diagnostics, "opaque_filter_unresolved",
                "An opaque Calendar Object Resource could not be classified by the requested semantic filters.");
        }
        return null;
    }

    private static FilterResult ApplyFilters(
        IReadOnlyList<CalendarResourceSnapshot> snapshots,
        CalendarEntityQuery query,
        ICollection<CalendarResourceDiagnostic> diagnostics,
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
            var temporal = CalendarEntityTemporalMatcher.Matches(
                snapshot,
                query.From,
                query.To,
                cancellationToken);
            occurrenceCount += temporal.OccurrenceCount;
            if (temporal.Match == CalendarEntityTemporalMatch.LimitExhausted || occurrenceCount > MaximumQueryOccurrences)
                return FilterResult.Failure(CalendarEntityQueryCode.LimitExhausted, occurrenceCount);
            if (temporal.Match == CalendarEntityTemporalMatch.Unresolved)
                return FilterResult.Failure(CalendarEntityQueryCode.TemporalUnresolved);
            if (temporal.Match == CalendarEntityTemporalMatch.Unevaluable)
                return FilterResult.Failure(CalendarEntityQueryCode.RecurrenceUnevaluable);
            if (temporal.Match == CalendarEntityTemporalMatch.NoMatch)
                continue;
            filtered.Add(snapshot);
        }
        return FilterResult.Success(filtered.OrderBy(snapshot => snapshot.CalendarHref, StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.ResourceHref, StringComparer.Ordinal)
            .ToArray());
    }

    private static bool MatchesKind(CalendarResourceSnapshot snapshot, IReadOnlyList<CalendarEntityKind> kinds) =>
        snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque
        || kinds.Any(kind => snapshot.Projection.Kind == ToProjectionKind(kind));

    private static void AddDiagnostic(
        ICollection<CalendarResourceDiagnostic> diagnostics,
        string code,
        string message)
    {
        if (diagnostics.Count < MaximumDiagnostics)
            diagnostics.Add(new CalendarResourceDiagnostic(code, message, CalendarResourceDiagnosticSeverity.Warning));
    }

    private CalendarEntityQueryResult? PrevalidateSelectedHref(CalendarEntityQuery query)
    {
        var href = query.Scope.Mode == CalendarEntityScopeMode.Selected ? query.Scope.Calendar?.Href : null;
        if (href is null)
            return null;
        if (!TryValidateCalendarHref(href))
            return CalendarEntityQueryResult.Failure(CalendarEntityQueryCode.UnsafeScope);
        var configuredScope = ParseScope(_options.CalendarHrefs);
        return configuredScope.Count > 0 && !configuredScope.Contains(href, StringComparer.Ordinal)
            ? CalendarEntityQueryResult.Failure(CalendarEntityQueryCode.OutsideScope)
            : null;
    }

    private bool TryValidateCalendarHref(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var candidate) || !HasSafeCalendarShape(candidate, href))
            return false;
        var origin = new Uri(_options.BaseUrl, UriKind.Absolute);
        return string.Equals(origin.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Host, candidate.Host, StringComparison.OrdinalIgnoreCase)
            && origin.Port == candidate.Port;
    }

    private static bool HasSafeCalendarShape(Uri candidate, string href) =>
        candidate.Scheme is "http" or "https"
        && string.IsNullOrEmpty(candidate.UserInfo)
        && string.IsNullOrEmpty(candidate.Fragment)
        && string.IsNullOrEmpty(candidate.Query)
        && !candidate.AbsolutePath.Contains("%2F", StringComparison.OrdinalIgnoreCase)
        && !candidate.AbsolutePath.Contains("%5C", StringComparison.OrdinalIgnoreCase)
        && !href.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        && !href.Contains('\\')
        && string.Equals(candidate.AbsoluteUri, href, StringComparison.Ordinal);

    private QuerySelectionPlan CreateSelectionPlan(
        CalendarEntityQuery query,
        IReadOnlyList<CalendarDescriptor> discovered,
        IReadOnlyList<CalendarDescriptor> scoped) => query.Scope.Mode switch
        {
            CalendarEntityScopeMode.Default => CreateDefaultSelectionPlan(query.EntityKinds, discovered, scoped),
            CalendarEntityScopeMode.Selected => CreateSelectedSelectionPlan(query, scoped),
            CalendarEntityScopeMode.All => CreateAllSelectionPlan(query.EntityKinds, scoped),
            _ => QuerySelectionPlan.Failure(CalendarEntityQueryCode.InvalidInput)
        };

    private QuerySelectionPlan CreateDefaultSelectionPlan(
        IReadOnlyList<CalendarEntityKind> kinds,
        IReadOnlyList<CalendarDescriptor> discovered,
        IReadOnlyList<CalendarDescriptor> scoped)
    {
        var selections = new List<(CalendarDescriptor Calendar, CalendarEntityKind Kind)>();
        foreach (var kind in kinds)
        {
            var selection = _resolveDefault(kind, discovered, scoped);
            if (selection.Code != CalendarSelectionCode.Success)
                return QuerySelectionPlan.Failure(MapSelectionCode(selection.Code), selection.Candidates);
            selections.Add((selection.Calendar!, kind));
        }
        return QuerySelectionPlan.Success(selections);
    }

    private static QuerySelectionPlan CreateSelectedSelectionPlan(
        CalendarEntityQuery query,
        IReadOnlyList<CalendarDescriptor> scoped)
    {
        var scopedMatches = FindCalendarMatches(scoped, query.Scope.Calendar!);
        if (scopedMatches.Length == 0)
            return QuerySelectionPlan.Failure(CalendarEntityQueryCode.NotFound, scoped.Take(MaximumDiagnostics).ToArray());
        if (scopedMatches.Length > 1)
            return QuerySelectionPlan.Failure(CalendarEntityQueryCode.Ambiguous, scopedMatches.Take(MaximumDiagnostics).ToArray());
        return CreateSelectedSuccess(query.EntityKinds, scopedMatches[0]);
    }

    private static QuerySelectionPlan CreateSelectedSuccess(
        IReadOnlyList<CalendarEntityKind> kinds,
        CalendarDescriptor calendar)
    {
        IReadOnlyList<CalendarResourceDiagnostic> diagnostics = kinds.Any(kind => !SupportsEntityKind(calendar, kind))
            ? [new CalendarResourceDiagnostic(
                "entity_kind_not_advertised",
                "The selected Calendar does not advertise one requested Entity Kind.",
                CalendarResourceDiagnosticSeverity.Warning)]
            : [];
        return QuerySelectionPlan.Success(kinds.Select(kind => (calendar, kind)).ToArray(), diagnostics);
    }

    private static QuerySelectionPlan CreateAllSelectionPlan(
        IReadOnlyList<CalendarEntityKind> kinds,
        IReadOnlyList<CalendarDescriptor> scoped) => QuerySelectionPlan.Success(scoped
            .SelectMany(calendar => kinds.Where(kind => SupportsEntityKind(calendar, kind)).Select(kind => (calendar, kind)))
            .ToArray());

    private static CalendarDescriptor[] FindCalendarMatches(
        IReadOnlyList<CalendarDescriptor> calendars,
        CalendarReference reference) => calendars.Where(calendar => reference.Name is not null
            ? string.Equals(calendar.DisplayName?.Trim(), reference.Name, StringComparison.OrdinalIgnoreCase)
            : string.Equals(calendar.Href, reference.Href, StringComparison.Ordinal)).ToArray();

    private static bool IsValidQueryShape(CalendarEntityQuery query) =>
        query.EntityKinds.Count is >= 1 and <= 2
        && query.EntityKinds.All(kind => kind is CalendarEntityKind.Event or CalendarEntityKind.Todo)
        && query.EntityKinds.Distinct().Count() == query.EntityKinds.Count
        && HasValidWindow(query.From, query.To)
        && query.Scope.Mode switch
        {
            CalendarEntityScopeMode.Default => query.Scope.Calendar is null,
            CalendarEntityScopeMode.Selected => HasExactlyOneSelector(query.Scope.Calendar),
            CalendarEntityScopeMode.All => query.Scope.Calendar is null,
            _ => false
        };

    private static bool HasValidWindow(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is null || to is null)
            return from is null && to is null;
        return from.Value.Offset == TimeSpan.Zero
            && to.Value.Offset == TimeSpan.Zero
            && to > from
            && to - from <= TimeSpan.FromDays(366);
    }

    private static bool HasExactlyOneSelector(CalendarReference? reference)
    {
        if (reference is null)
            return false;
        var hasName = !string.IsNullOrWhiteSpace(reference.Name);
        var hasHref = !string.IsNullOrWhiteSpace(reference.Href);
        return hasName != hasHref && (!hasName || IsCanonicalName(reference.Name));
    }

    private static bool IsCanonicalName(string? name) => !string.IsNullOrWhiteSpace(name)
        && string.Equals(name, name.Trim(), StringComparison.Ordinal);

    private static bool SupportsEntityKind(CalendarDescriptor calendar, CalendarEntityKind entityKind) => entityKind switch
    {
        CalendarEntityKind.Event => calendar.EventSupport != EntityKindSupport.NotAdvertised,
        CalendarEntityKind.Todo => calendar.TodoSupport != EntityKindSupport.NotAdvertised,
        _ => false
    };

    private static CalendarResourceProjectionKind ToProjectionKind(CalendarEntityKind kind) => kind switch
    {
        CalendarEntityKind.Event => CalendarResourceProjectionKind.Event,
        _ => CalendarResourceProjectionKind.Todo
    };

    private static CalendarEntityQueryCode MapSelectionCode(CalendarSelectionCode code) => code switch
    {
        CalendarSelectionCode.NotFound => CalendarEntityQueryCode.NotFound,
        CalendarSelectionCode.Ambiguous => CalendarEntityQueryCode.Ambiguous,
        CalendarSelectionCode.OutsideScope => CalendarEntityQueryCode.OutsideScope,
        _ => CalendarEntityQueryCode.UnsupportedCapability
    };

    private static CalendarEntityQueryCode MapReadFailure(CalendarResourceReadCode code) => code switch
    {
        CalendarResourceReadCode.ConcurrencyUnavailable => CalendarEntityQueryCode.ConcurrencyUnavailable,
        CalendarResourceReadCode.PayloadTooLarge => CalendarEntityQueryCode.PayloadTooLarge,
        _ => CalendarEntityQueryCode.UpstreamProtocolError
    };

    private static CalendarResourceDiagnostic ToResourceDiagnostic(CalendarDiagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.Severity switch
        {
            CalendarDiagnosticSeverity.Info => CalendarResourceDiagnosticSeverity.Info,
            CalendarDiagnosticSeverity.Warning => CalendarResourceDiagnosticSeverity.Warning,
            _ => CalendarResourceDiagnosticSeverity.Error
        });

    private static IReadOnlyList<string> ParseScope(string? calendarHrefs) => calendarHrefs is null
        ? []
        : calendarHrefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record CandidateFetchResult(
        CalendarEntityQueryCode Code,
        IReadOnlyList<CalendarResourceSnapshot> Snapshots,
        List<CalendarResourceDiagnostic> Diagnostics,
        CalendarEntityQueryExecutionLimits? Limits)
    {
        public static CandidateFetchResult Success(
            IReadOnlyList<CalendarResourceSnapshot> snapshots,
            List<CalendarResourceDiagnostic> diagnostics) => new(CalendarEntityQueryCode.Success, snapshots, diagnostics, null);

        public static CandidateFetchResult Failure(
            CalendarEntityQueryCode code,
            CalendarEntityQueryExecutionLimits? limits = null) => new(code, [], [], limits);
    }

    private sealed record FilterResult(
        CalendarEntityQueryCode Code,
        IReadOnlyList<CalendarResourceSnapshot> Snapshots,
        int OccurrenceCount)
    {
        public static FilterResult Success(IReadOnlyList<CalendarResourceSnapshot> snapshots) =>
            new(CalendarEntityQueryCode.Success, snapshots, 0);

        public static FilterResult Failure(CalendarEntityQueryCode code, int occurrenceCount = 0) =>
            new(code, [], occurrenceCount);
    }

    private sealed record QuerySelectionPlan(
        CalendarEntityQueryCode Code,
        IReadOnlyList<(CalendarDescriptor Calendar, CalendarEntityKind Kind)> Selections,
        IReadOnlyList<CalendarResourceDiagnostic> Diagnostics,
        IReadOnlyList<CalendarDescriptor> AuthorizedCandidates)
    {
        public static QuerySelectionPlan Success(
            IReadOnlyList<(CalendarDescriptor Calendar, CalendarEntityKind Kind)> selections,
            IReadOnlyList<CalendarResourceDiagnostic>? diagnostics = null) =>
            new(CalendarEntityQueryCode.Success, selections, diagnostics ?? [], []);

        public static QuerySelectionPlan Failure(
            CalendarEntityQueryCode code,
            IReadOnlyList<CalendarDescriptor>? authorizedCandidates = null) => new(code, [], [], authorizedCandidates ?? []);
    }
}
