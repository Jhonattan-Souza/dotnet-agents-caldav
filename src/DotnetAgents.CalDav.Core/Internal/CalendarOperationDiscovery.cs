using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Coordinates one immutable CalDAV discovery acquisition per authorization-bound key and operation.</summary>
internal sealed class CalendarOperationDiscovery : ICalendarClient, ICalendarCreateTransport
{
    private readonly ICalendarClient _transport;
    private readonly IOptions<CalDavOptions> _options;
    private readonly Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> _applyScope;
    private readonly Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
        _resolveDefault;
    private readonly ConcurrentDictionary<CalendarDiscoveryKey, DiscoveryAcquisition> _results = new();

    public CalendarOperationDiscovery(
        ICalendarClient transport,
        IOptions<CalDavOptions> options,
        Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> applyScope,
        Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
            resolveDefault)
    {
        _transport = transport;
        _options = options;
        _applyScope = applyScope;
        _resolveDefault = resolveDefault;
    }

    public void RediscoverCapabilities() => _transport.RediscoverCapabilities();

    public async Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken)
    {
        return (await GetResultAsync(cancellationToken).ConfigureAwait(false)).Discovery.Items;
    }

    public async Task<CalendarDiscoveryResult> GetScopedResultAsync(CancellationToken cancellationToken) =>
        (await GetResultAsync(cancellationToken).ConfigureAwait(false)).Discovery;

    public CalendarSelectionResult ResolveDefault(CalendarEntityKind entityKind)
    {
        var key = CalendarDiscoveryKey.Create(_options.Value);
        if (!_results.TryGetValue(key, out var acquisition))
            throw new InvalidOperationException("Calendar discovery must complete before default selection.");
        var result = acquisition.GetCompletedResult();
        return entityKind switch
        {
            CalendarEntityKind.Event => result.EventDefault,
            CalendarEntityKind.Todo => result.TodoDefault,
            _ => CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound)
        };
    }

    public Task<IReadOnlyList<string>> QueryCalendarResourceHrefsAsync(
        string calendarHref,
        CalendarEntityKind entityKind,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken) => _transport.QueryCalendarResourceHrefsAsync(
            calendarHref,
            entityKind,
            from,
            to,
            cancellationToken);

    public Task<CalendarResourceRead> GetCalendarResourceAsync(
        string href,
        CancellationToken cancellationToken) => _transport.GetCalendarResourceAsync(href, cancellationToken);

    public Task<CalendarResourceRead> GetCalendarResourceForQueryAsync(
        string calendarHref,
        string href,
        CancellationToken cancellationToken) => _transport.GetCalendarResourceForQueryAsync(
            calendarHref,
            href,
            cancellationToken);

    public Task<IReadOnlyList<CalendarResourceRead>> GetCalendarResourcesForQueryAsync(
        string calendarHref,
        IReadOnlyList<string> hrefs,
        CancellationToken cancellationToken) => _transport.GetCalendarResourcesForQueryAsync(
            calendarHref,
            hrefs,
            cancellationToken);

    public Task<CalendarResourceCreateResult> CreateCalendarResourceAsync(
        CalendarResourceCreateRequest request,
        CancellationToken cancellationToken) => _transport.CreateCalendarResourceAsync(request, cancellationToken);

    public Task<CalendarResourceDeleteDispatchResult> DeleteCalendarResourceAsync(
        CalendarResourceDeleteRequest request,
        CancellationToken cancellationToken) => _transport.DeleteCalendarResourceAsync(request, cancellationToken);

    public Task<CalendarResourceMoveDispatchResult> MoveCalendarResourceAsync(
        CalendarResourceMoveDispatchRequest request,
        CancellationToken cancellationToken) => _transport.MoveCalendarResourceAsync(request, cancellationToken);

    public Task<CalendarResourceUpdateDispatchResult> UpdateCalendarResourceAsync(
        CalendarResourceUpdateRequest request,
        CancellationToken cancellationToken) => _transport.UpdateCalendarResourceAsync(request, cancellationToken);

    private async Task<CalendarOperationDiscoveryResult> AcquireAsync(
        CalendarDiscoveryKey key,
        CancellationToken cancellationToken)
    {
        var calendars = await _transport.GetCalendarsAsync(cancellationToken).ConfigureAwait(false);
        var scoped = _applyScope(calendars);
        var frozenItems = scoped.Items.Select(Freeze).ToImmutableArray();
        var frozenDiscovery = new CalendarDiscoveryResult(
            frozenItems,
            scoped.Diagnostics.ToImmutableArray());
        return new CalendarOperationDiscoveryResult(
            key,
            frozenDiscovery,
            _resolveDefault(CalendarEntityKind.Event, calendars, frozenItems),
            _resolveDefault(CalendarEntityKind.Todo, calendars, frozenItems));
    }

    private Task<CalendarOperationDiscoveryResult> GetResultAsync(CancellationToken cancellationToken)
    {
        var key = CalendarDiscoveryKey.Create(_options.Value);
        var acquisition = _results.GetOrAdd(
            key,
            _ => new DiscoveryAcquisition(token => AcquireAsync(key, token)));
        return acquisition.WaitAsync(cancellationToken);
    }

    private static CalendarDescriptor Freeze(CalendarDescriptor calendar) => calendar with
    {
        EventEvidence = calendar.EventEvidence.ToArray(),
        TodoEvidence = calendar.TodoEvidence.ToArray(),
        UnavailableProperties = calendar.UnavailableProperties.ToArray()
    };

    private sealed class DiscoveryAcquisition
    {
        private readonly CancellationTokenSource _operationCancellation = new();
        private readonly Lazy<Task<CalendarOperationDiscoveryResult>> _result;
        private int _activeConsumers;
        private int _disposed;

        public DiscoveryAcquisition(Func<CancellationToken, Task<CalendarOperationDiscoveryResult>> acquire)
        {
            _result = new Lazy<Task<CalendarOperationDiscoveryResult>>(
                () => acquire(_operationCancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public async Task<CalendarOperationDiscoveryResult> WaitAsync(CancellationToken consumerCancellation)
        {
            Interlocked.Increment(ref _activeConsumers);
            var result = _result.Value;
            try
            {
                return await result.WaitAsync(consumerCancellation).ConfigureAwait(false);
            }
            finally
            {
                if (Interlocked.Decrement(ref _activeConsumers) == 0)
                {
                    if (!result.IsCompleted)
                        await _operationCancellation.CancelAsync().ConfigureAwait(false);
                    if (Interlocked.Exchange(ref _disposed, 1) == 0)
                        _operationCancellation.Dispose();
                }
            }
        }

        public CalendarOperationDiscoveryResult GetCompletedResult()
        {
            var result = _result.IsValueCreated ? _result.Value : null;
            return result is { IsCompletedSuccessfully: true }
                ? result.Result
                : throw new InvalidOperationException("Calendar discovery must complete before default selection.");
        }
    }
}

internal sealed record CalendarOperationDiscoveryResult(
    CalendarDiscoveryKey Key,
    CalendarDiscoveryResult Discovery,
    CalendarSelectionResult EventDefault,
    CalendarSelectionResult TodoDefault);

internal readonly record struct CalendarDiscoveryKey(
    string Origin,
    string PrincipalIdentity,
    long CredentialGeneration,
    string DiscoveryEndpoint,
    string CalendarScope,
    string DefaultEventCalendarName,
    string DefaultTodoCalendarName,
    long RequestTimeoutTicks)
{
    public static CalendarDiscoveryKey Create(CalDavOptions options)
    {
        var endpoint = new Uri(options.BaseUrl, UriKind.Absolute);
        var origin = endpoint.GetLeftPart(UriPartial.Authority);
        var scope = string.Join(',', (options.CalendarHrefs ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
        return new CalendarDiscoveryKey(
            origin,
            options.Username,
            CalendarCredentialGeneration.Get(options.Password),
            endpoint.AbsoluteUri,
            scope,
            options.DefaultEventCalendarName?.Trim() ?? string.Empty,
            options.DefaultTodoCalendarName?.Trim() ?? string.Empty,
            options.RequestTimeout.Ticks);
    }
}

internal static class CalendarCredentialGeneration
{
    private static readonly ConditionalWeakTable<string, Generation> Generations = new();
    private static long _next;

    public static long Get(string credential) => Generations.GetValue(
        credential,
        _ => new Generation(Interlocked.Increment(ref _next))).Value;

    private sealed record Generation(long Value);
}
