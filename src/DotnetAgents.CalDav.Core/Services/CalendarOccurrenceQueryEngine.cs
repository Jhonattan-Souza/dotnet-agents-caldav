using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using NodaTime;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Owns bounded Occurrence evaluation over authoritative local resource snapshots.</summary>
internal sealed class CalendarOccurrenceQueryEngine
{
    private const int MaximumQueryOccurrences = 5000;
    private readonly CalendarEntityQueryEngine _entityQueryEngine;

    public CalendarOccurrenceQueryEngine(
        ICalendarClient calendarClient,
        CalDavOptions options,
        Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> applyScope,
        Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
            resolveDefault)
    {
        _entityQueryEngine = new CalendarEntityQueryEngine(calendarClient, options, applyScope, resolveDefault);
    }

    public async Task<CalendarOccurrenceQueryResult> QueryAsync(
        CalendarOccurrenceQuery query,
        CancellationToken cancellationToken)
    {
        if (!IsValid(query))
            return CalendarOccurrenceQueryResult.Failure(CalendarOccurrenceQueryCode.InvalidInput);

        var resources = await _entityQueryEngine.QueryAsync(
            new CalendarEntityQuery(query.Scope, [CalendarEntityKind.Event, CalendarEntityKind.Todo]),
            cancellationToken);
        if (resources.Code != CalendarEntityQueryCode.Success)
            return MapResourceFailure(resources);

        var items = new List<CalendarOccurrenceSnapshot>();
        var observedCount = 0;
        foreach (var snapshot in resources.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CalendarOccurrenceEvaluator.HasInvalidComponentStructure(snapshot))
            {
                return CalendarOccurrenceQueryResult.Failure(
                    CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
            }
            if (snapshot.Projection.Kind == CalendarResourceProjectionKind.Opaque)
                continue;
            var evaluated = CalendarOccurrenceEvaluator.Evaluate(snapshot, query, cancellationToken);
            observedCount += evaluated.ObservedOccurrenceCount;
            if (evaluated.Code != CalendarOccurrenceQueryCode.Success)
            {
                return CalendarOccurrenceQueryResult.Failure(
                    evaluated.Code,
                    limits: evaluated.Code == CalendarOccurrenceQueryCode.LimitExhausted && observedCount > 0
                        ? new CalendarOccurrenceQueryExecutionLimits(OccurrenceCount: observedCount)
                        : null);
            }
            if (observedCount > MaximumQueryOccurrences)
            {
                return CalendarOccurrenceQueryResult.Failure(
                    CalendarOccurrenceQueryCode.LimitExhausted,
                    limits: new CalendarOccurrenceQueryExecutionLimits(OccurrenceCount: observedCount));
            }
            items.AddRange(evaluated.Items);
        }

        return CalendarOccurrenceQueryResult.Success(
            items.OrderBy(item => item.Timing.EvaluatedStartUtc!.Value, StringComparer.Ordinal)
                .ThenBy(item => item.Snapshot.CalendarHref, StringComparer.Ordinal)
                .ThenBy(item => item.Snapshot.Projection.EntityUid, StringComparer.Ordinal)
                .ThenBy(item => CalendarOccurrenceEvaluator.GetIdentitySortKey(item.RecurrenceIdentity), StringComparer.Ordinal)
                .ToArray(),
            resources.Diagnostics);
    }

    private static bool IsValid(CalendarOccurrenceQuery query) =>
        query.From.Offset == TimeSpan.Zero
        && query.To.Offset == TimeSpan.Zero
        && query.To > query.From
        && query.To - query.From <= TimeSpan.FromDays(366)
        && (query.EvaluationTimeZone is null
            || DateTimeZoneProviders.Tzdb.GetZoneOrNull(query.EvaluationTimeZone) is not null);

    private static CalendarOccurrenceQueryResult MapResourceFailure(CalendarEntityQueryResult result) =>
        CalendarOccurrenceQueryResult.Failure(
            result.Code switch
            {
                CalendarEntityQueryCode.InvalidInput => CalendarOccurrenceQueryCode.InvalidInput,
                CalendarEntityQueryCode.UnsafeScope => CalendarOccurrenceQueryCode.UnsafeScope,
                CalendarEntityQueryCode.NotFound => CalendarOccurrenceQueryCode.NotFound,
                CalendarEntityQueryCode.Ambiguous => CalendarOccurrenceQueryCode.Ambiguous,
                CalendarEntityQueryCode.OutsideScope => CalendarOccurrenceQueryCode.OutsideScope,
                CalendarEntityQueryCode.UnsupportedCapability => CalendarOccurrenceQueryCode.UnsupportedCapability,
                CalendarEntityQueryCode.ConcurrencyUnavailable => CalendarOccurrenceQueryCode.ConcurrencyUnavailable,
                CalendarEntityQueryCode.LimitExhausted => CalendarOccurrenceQueryCode.LimitExhausted,
                CalendarEntityQueryCode.PayloadTooLarge => CalendarOccurrenceQueryCode.PayloadTooLarge,
                CalendarEntityQueryCode.TemporalUnresolved => CalendarOccurrenceQueryCode.TemporalUnresolved,
                CalendarEntityQueryCode.RecurrenceUnevaluable => CalendarOccurrenceQueryCode.RecurrenceUnevaluable,
                _ => CalendarOccurrenceQueryCode.UpstreamProtocolError
            },
            result.AuthorizedCandidates,
            result.Limits is null
                ? null
                : new CalendarOccurrenceQueryExecutionLimits(
                    result.Limits.ResourcesInspected,
                    result.Limits.OccurrenceCount,
                    result.Limits.ByteCount));
}
