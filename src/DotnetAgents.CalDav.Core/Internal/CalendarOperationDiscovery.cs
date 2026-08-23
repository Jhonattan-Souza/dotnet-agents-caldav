using System.Collections.Concurrent;
using System.Collections.Immutable;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Coordinates one immutable CalDAV discovery acquisition per authorization-bound key and operation.</summary>
internal sealed class CalendarOperationDiscovery : ICalendarClient, ICalendarCreateTransport, ICalendarMoveTransport
{
    private readonly ICalendarClient _transport;
    private readonly Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> _applyScope;
    private readonly Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
        _resolveDefault;
    private readonly CalendarDiscoveryKey _key;
    private readonly ConcurrentDictionary<CalendarDiscoveryKey, Lazy<Task<CalendarOperationDiscoveryResult>>> _results = new();

    public CalendarOperationDiscovery(
        ICalendarClient transport,
        IOptions<CalDavOptions> options,
        Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> applyScope,
        Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
            resolveDefault)
    {
        _transport = transport;
        _applyScope = applyScope;
        _resolveDefault = resolveDefault;
        _key = CalendarDiscoveryKey.Create(options.Value, CalendarOperationContextGeneration.Create());
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
        if (!_results.TryGetValue(_key, out var acquisition))
            throw new InvalidOperationException("Calendar discovery must complete before default selection.");
        var task = acquisition.IsValueCreated ? acquisition.Value : null;
        var result = task is { IsCompletedSuccessfully: true }
            ? task.Result
            : throw new InvalidOperationException("Calendar discovery must complete before default selection.");
        return entityKind switch
        {
            CalendarEntityKind.Event => result.EventDefault,
            CalendarEntityKind.Todo => result.TodoDefault,
            _ => CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound)
        };
    }

    public Task<CalendarResourceRead> GetCalendarResourceAsync(
        string href,
        CancellationToken cancellationToken) => _transport.GetCalendarResourceAsync(href, cancellationToken);

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

    async Task<CalendarMoveDiscoveryResult> ICalendarMoveTransport.DiscoverCalendarsAsync(
        CancellationToken cancellationToken)
    {
        var result = await GetResultAsync(cancellationToken).ConfigureAwait(false);
        return new CalendarMoveDiscoveryResult(
            result.Discovery,
            result.EventDefault,
            result.TodoDefault);
    }

    Task<CalendarResourceRead> ICalendarMoveTransport.ReadSourceAsync(
        string sourceCalendarHref,
        string href,
        CancellationToken cancellationToken) => _transport is ICalendarMoveResourceTransport moveTransport
            ? moveTransport.ReadMoveResourceAsync(sourceCalendarHref, href, absenceProbe: false, cancellationToken)
            : Task.FromResult(new CalendarResourceRead(CalendarResourceReadCode.UnsupportedCapability));

    Task<CalendarResourceRead> ICalendarMoveTransport.ProbeDestinationPresenceAsync(
        string destinationCalendarHref,
        string href,
        CancellationToken cancellationToken) => _transport is ICalendarMoveResourceTransport moveTransport
            ? moveTransport.ProbeMoveResourcePresenceAsync(destinationCalendarHref, href, cancellationToken)
            : Task.FromResult(new CalendarResourceRead(CalendarResourceReadCode.UnsupportedCapability));

    Task<CalendarResourceRead> ICalendarMoveTransport.ObserveResourceAsync(
        string authorizedCalendarHref,
        string href,
        CancellationToken cancellationToken) => ProbeThroughReadAsync(
        authorizedCalendarHref,
        href,
        cancellationToken);

    Task<CalendarResourceMoveDispatchResult> ICalendarMoveTransport.DispatchAsync(
        string sourceCalendarHref,
        string destinationCalendarHref,
        CalendarResourceMoveDispatchRequest request,
        CancellationToken cancellationToken) => _transport is ICalendarMoveResourceTransport moveTransport
            ? moveTransport.DispatchMoveAsync(
                sourceCalendarHref,
                destinationCalendarHref,
                request,
                cancellationToken)
            : Task.FromResult(new CalendarResourceMoveDispatchResult(
                CalendarResourceMoveDispatchCode.UnsupportedCapability));

    private async Task<CalendarResourceRead> ProbeThroughReadAsync(
        string authorizedCalendarHref,
        string href,
        CancellationToken cancellationToken)
    {
        using var scope = CalendarHttpTelemetry.BeginAbsenceProbe();
        return _transport is ICalendarMoveResourceTransport moveTransport
            ? await moveTransport.ReadMoveResourceAsync(
                authorizedCalendarHref,
                href,
                absenceProbe: true,
                cancellationToken)
            : new CalendarResourceRead(CalendarResourceReadCode.UnsupportedCapability);
    }

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
        var acquisition = _results.GetOrAdd(
            _key,
            key => new Lazy<Task<CalendarOperationDiscoveryResult>>(
                () => AcquireAsync(key, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return acquisition.Value;
    }

    private static CalendarDescriptor Freeze(CalendarDescriptor calendar) => calendar with
    {
        EventEvidence = calendar.EventEvidence.ToArray(),
        TodoEvidence = calendar.TodoEvidence.ToArray(),
        UnavailableProperties = calendar.UnavailableProperties.ToArray()
    };

}

internal sealed record CalendarOperationDiscoveryResult(
    CalendarDiscoveryKey Key,
    CalendarDiscoveryResult Discovery,
    CalendarSelectionResult EventDefault,
    CalendarSelectionResult TodoDefault);

internal readonly record struct CalendarDiscoveryKey(
    string Origin,
    string PrincipalIdentity,
    CalendarOperationContextGeneration OperationContextGeneration,
    string DiscoveryEndpoint,
    string CalendarScope,
    string DefaultEventCalendarName,
    string DefaultTodoCalendarName,
    long RequestTimeoutTicks)
{
    public static CalendarDiscoveryKey Create(
        CalDavOptions options,
        CalendarOperationContextGeneration operationContextGeneration)
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
            operationContextGeneration,
            endpoint.AbsoluteUri,
            scope,
            options.DefaultEventCalendarName?.Trim() ?? string.Empty,
            options.DefaultTodoCalendarName?.Trim() ?? string.Empty,
            options.RequestTimeout.Ticks);
    }
}

internal sealed class CalendarOperationContextGeneration
{
    private CalendarOperationContextGeneration()
    {
    }

    public static CalendarOperationContextGeneration Create() => new();
}
