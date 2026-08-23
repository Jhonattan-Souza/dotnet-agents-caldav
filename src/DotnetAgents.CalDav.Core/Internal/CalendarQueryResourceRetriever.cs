using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Xml;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>
/// Owns authoritative query retrieval after candidate selection, including the bounded
/// compatibility path for Calendars that prove Calendar multiget unavailable.
/// </summary>
internal sealed class CalendarQueryResourceRetriever
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _originPermits = new(StringComparer.Ordinal);

    internal async Task<CalendarQueryRetrievalResult> RetrieveAsync(
        ICalendarQueryTransport transport,
        IReadOnlyDictionary<string, CalendarDescriptor> candidates,
        CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(transport, candidates, cancellationToken).ConfigureAwait(false);
        if (plan.Error is not null)
            return CalendarQueryRetrievalResult.Failure(plan.Error);
        var retrieved = plan.Resources;
        var fallback = plan.Fallback;
        if (fallback.Count == 0)
            return Successful(retrieved);
        if (fallback.Count > CalendarQueryPolicy.MaximumDirectGetResources)
        {
            return CalendarQueryRetrievalResult.Failure(CalendarQueryFailures.Limit(
                "Direct GET Compatibility Mode exhausted its resource budget.",
                new QueryExecutionLimits(
                    Dimension: QueryLimitDimension.ResourceCount,
                    Observed: fallback.Count,
                    Limit: CalendarQueryPolicy.MaximumDirectGetResources)));
        }

        var budget = new CalendarDirectGetBudget();
        foreach (var wave in fallback.OrderBy(candidate => candidate.ResourceHref, StringComparer.Ordinal)
                     .Chunk(CalendarQueryPolicy.MaximumDirectGetConcurrency))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reads = await Task.WhenAll(wave.Select(candidate => ReadAsync(
                transport,
                candidate.Calendar,
                candidate.ResourceHref,
                budget,
                cancellationToken))).ConfigureAwait(false);
            if (budget.Failure is { } budgetFailure)
                return CalendarQueryRetrievalResult.Failure(budgetFailure);
            var failure = reads.Select(result =>
                    result.Error ?? (string.Equals(result.RequestedHref, result.Read.ResourceHref, StringComparison.Ordinal)
                        ? ReadFailure(result.Read)
                        : CalendarQueryFailures.Protocol()))
                .FirstOrDefault(error => error is not null);
            if (failure is not null)
                return CalendarQueryRetrievalResult.Failure(failure);
            retrieved.AddRange(reads);
        }
        return Successful(retrieved);
    }

    private static async Task<CalendarQueryRetrievalPlan> PlanAsync(
        ICalendarQueryTransport transport,
        IReadOnlyDictionary<string, CalendarDescriptor> candidates,
        CancellationToken cancellationToken)
    {
        var retrieved = new List<CalendarQueryResourceRetrieval>(candidates.Count);
        var fallback = new List<(CalendarDescriptor Calendar, string ResourceHref)>();
        foreach (var group in candidates.GroupBy(candidate => candidate.Value.Href, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var calendar = group.First().Value;
            var calendarCandidates = group.Select(candidate => candidate.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var calendarReads = new List<CalendarQueryResourceRetrieval>(calendarCandidates.Length);
            var unavailable = false;
            foreach (var batch in calendarCandidates.Chunk(CalendarQueryPolicy.MaximumMultigetBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attempt = await AttemptMultigetAsync(
                    transport,
                    calendar.Href,
                    batch,
                    cancellationToken).ConfigureAwait(false);
                if (attempt.Error is not null)
                    return CalendarQueryRetrievalPlan.Failure(attempt.Error);
                var result = attempt.Result!;
                if (result is CalendarMultigetResult.VerifiedUnavailable)
                {
                    unavailable = true;
                    break;
                }
                CalendarQueryTelemetry.ObserveMultigetSuccess();
                var reads = ExactResponses(batch, ((CalendarMultigetResult.Resources)result).Values);
                if (reads is null)
                    return CalendarQueryRetrievalPlan.Failure(CalendarQueryFailures.Protocol());
                var failure = reads.Select(ReadFailure).FirstOrDefault(error => error is not null);
                if (failure is not null)
                    return CalendarQueryRetrievalPlan.Failure(failure);
                calendarReads.AddRange(reads.Select((read, index) =>
                    new CalendarQueryResourceRetrieval(calendar, batch[index], read)));
            }
            if (unavailable)
                fallback.AddRange(calendarCandidates.Select(href => (calendar, href)));
            else
                retrieved.AddRange(calendarReads);
        }

        return CalendarQueryRetrievalPlan.Success(retrieved, fallback);
    }

    private static async Task<CalendarMultigetAttempt> AttemptMultigetAsync(
        ICalendarQueryTransport transport,
        string calendarHref,
        IReadOnlyList<string> batch,
        CancellationToken cancellationToken)
    {
        try
        {
            return CalendarMultigetAttempt.Success(await transport.MultigetAsync(
                calendarHref,
                batch,
                cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException exception)
        {
            return CalendarMultigetAttempt.Failure(CalendarQueryFailures.FromHttp(exception.StatusCode));
        }
        catch (Exception exception) when (exception is TimeoutException or TimeoutRejectedException
                                           or BrokenCircuitException or IOException)
        {
            return CalendarMultigetAttempt.Failure(CalendarQueryFailures.UpstreamUnavailable());
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return CalendarMultigetAttempt.Failure(CalendarQueryFailures.UnsupportedCapability());
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return CalendarMultigetAttempt.Failure(CalendarQueryFailures.Protocol());
        }
    }

    private async Task<CalendarQueryResourceRetrieval> ReadAsync(
        ICalendarQueryTransport transport,
        CalendarDescriptor calendar,
        string resourceHref,
        CalendarDirectGetBudget budget,
        CancellationToken cancellationToken)
    {
        var originPermits = _originPermits.GetOrAdd(
            new Uri(calendar.Href).GetLeftPart(UriPartial.Authority),
            static _ => new SemaphoreSlim(
                CalendarQueryPolicy.MaximumDirectGetConcurrency,
                CalendarQueryPolicy.MaximumDirectGetConcurrency));
        await originPermits.WaitAsync(cancellationToken).ConfigureAwait(false);
        var meter = budget.StartResource();
        try
        {
            CalendarQueryTelemetry.ObserveDirectGetFallback();
            CalendarQueryTelemetry.Add("caldav.query.direct_get_resource_count");
            using var purpose = CalendarHttpTelemetry.BeginQueryResourceRead(meter);
            var read = await transport.GetAsync(calendar.Href, resourceHref, cancellationToken).ConfigureAwait(false);
            if (meter.Attempts == 0)
                meter.RecordSyntheticAttempt(read.AuthoritativeUtf8.Length);
            return new CalendarQueryResourceRetrieval(calendar, resourceHref, read, meter.Failure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            EnsureSyntheticAttempt(meter);
            return FailedRead(calendar, resourceHref, MeterFailureOr(
                meter,
                CalendarQueryFailures.UpstreamUnavailable()));
        }
        catch (HttpRequestException exception)
        {
            EnsureSyntheticAttempt(meter);
            if (meter.Attempts == CalendarDirectGetBudget.MaximumAttemptsPerResource)
            {
                return FailedRead(calendar, resourceHref, MeterFailureOr(
                    meter,
                    CalendarDirectGetBudget.Limit(
                        QueryLimitDimension.AttemptCount,
                        meter.Attempts,
                        CalendarDirectGetBudget.MaximumAttemptsPerResource)));
            }
            return FailedRead(calendar, resourceHref, MeterFailureOr(
                meter,
                CalendarQueryFailures.FromHttp(exception.StatusCode)));
        }
        catch (CalendarDirectGetAttemptLimitException)
        {
            return FailedRead(
                calendar,
                resourceHref,
                meter.Failure ?? CalendarDirectGetBudget.Limit(
                    QueryLimitDimension.AttemptCount,
                    meter.Attempts,
                    CalendarDirectGetBudget.MaximumAttemptsPerResource));
        }
        catch (CalendarDirectGetBudgetExceededException)
        {
            return FailedRead(
                calendar,
                resourceHref,
                meter.Failure ?? CalendarQueryFailures.Protocol());
        }
        catch (Exception exception) when (exception is TimeoutException or TimeoutRejectedException)
        {
            EnsureSyntheticAttempt(meter);
            return FailedRead(calendar, resourceHref, MeterFailureOr(
                meter,
                meter.Attempts == CalendarDirectGetBudget.MaximumAttemptsPerResource
                    ? CalendarDirectGetBudget.Limit(
                        QueryLimitDimension.AttemptCount,
                        meter.Attempts,
                        CalendarDirectGetBudget.MaximumAttemptsPerResource)
                    : CalendarQueryFailures.UpstreamUnavailable()));
        }
        catch (BrokenCircuitException)
        {
            return FailedRead(calendar, resourceHref, MeterFailureOr(
                meter,
                CalendarQueryFailures.UpstreamUnavailable()));
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            EnsureSyntheticAttempt(meter);
            return FailedRead(calendar, resourceHref, MeterFailureOr(
                meter,
                CalendarQueryFailures.UnsupportedCapability()));
        }
        catch (IOException)
        {
            EnsureSyntheticAttempt(meter);
            return FailedRead(calendar, resourceHref, MeterFailureOr(
                meter,
                CalendarQueryFailures.UpstreamUnavailable()));
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            EnsureSyntheticAttempt(meter);
            return FailedRead(calendar, resourceHref, MeterFailureOr(
                meter,
                CalendarQueryFailures.Protocol()));
        }
        finally
        {
            originPermits.Release();
        }
    }

    private static void EnsureSyntheticAttempt(CalendarDirectGetReadMeter meter)
    {
        if (meter.Attempts == 0)
            meter.RecordSyntheticAttempt(0);
    }

    private static QueryFailure MeterFailureOr(CalendarDirectGetReadMeter meter, QueryFailure fallback) =>
        meter.Failure ?? fallback;

    private static CalendarQueryRetrievalResult Successful(
        IReadOnlyList<CalendarQueryResourceRetrieval> resources)
    {
        CalendarQueryTelemetry.Add(
            "caldav.query.disappeared_resource_count",
            resources.Where(resource => resource.Read.Code == CalendarResourceReadCode.NotFound)
                .Select(resource => resource.RequestedHref)
                .Distinct(StringComparer.Ordinal)
                .Count());
        return CalendarQueryRetrievalResult.Success(resources);
    }

    private static CalendarQueryResourceRetrieval FailedRead(
        CalendarDescriptor calendar,
        string resourceHref,
        QueryFailure failure) => new(
            calendar,
            resourceHref,
            new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError, resourceHref),
            failure);

    private static IReadOnlyList<CalendarResourceRead>? ExactResponses(
        IReadOnlyList<string> requested,
        IReadOnlyList<CalendarResourceRead> reads)
    {
        if (reads.Count != requested.Count)
            return null;
        var requestedSet = requested.ToHashSet(StringComparer.Ordinal);
        var indexed = new Dictionary<string, CalendarResourceRead>(StringComparer.Ordinal);
        foreach (var read in reads)
        {
            if (read.ResourceHref is null
                || !requestedSet.Contains(read.ResourceHref)
                || !indexed.TryAdd(read.ResourceHref, read))
                return null;
        }
        return requested.Select(href => indexed[href]).ToArray();
    }

    private static QueryFailure? ReadFailure(CalendarResourceRead read) => read.Code switch
    {
        CalendarResourceReadCode.Success when HasStrongEntityTag(read.EntityTag) => null,
        CalendarResourceReadCode.NotFound => null,
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
}

internal sealed record CalendarQueryRetrievalPlan(
    List<CalendarQueryResourceRetrieval> Resources,
    List<(CalendarDescriptor Calendar, string ResourceHref)> Fallback,
    QueryFailure? Error)
{
    internal static CalendarQueryRetrievalPlan Success(
        List<CalendarQueryResourceRetrieval> resources,
        List<(CalendarDescriptor Calendar, string ResourceHref)> fallback) => new(resources, fallback, null);

    internal static CalendarQueryRetrievalPlan Failure(QueryFailure error) => new([], [], error);
}

internal sealed record CalendarMultigetAttempt(CalendarMultigetResult? Result, QueryFailure? Error)
{
    internal static CalendarMultigetAttempt Success(CalendarMultigetResult result) => new(result, null);

    internal static CalendarMultigetAttempt Failure(QueryFailure error) => new(null, error);
}

internal sealed record CalendarQueryResourceRetrieval(
    CalendarDescriptor Calendar,
    string RequestedHref,
    CalendarResourceRead Read,
    QueryFailure? Error = null);

internal sealed record CalendarQueryRetrievalResult(
    IReadOnlyList<CalendarQueryResourceRetrieval> Resources,
    QueryFailure? Error)
{
    internal static CalendarQueryRetrievalResult Success(
        IReadOnlyList<CalendarQueryResourceRetrieval> resources) => new(resources, null);

    internal static CalendarQueryRetrievalResult Failure(QueryFailure error) => new([], error);
}
