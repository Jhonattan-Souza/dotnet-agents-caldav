using System.Collections.Immutable;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>True-external seam for one CalDAV Calendar discovery acquisition.</summary>
internal interface ICalendarDiscoveryTransport
{
    Task<IReadOnlyList<CalendarDescriptor>> DiscoverAsync(CancellationToken cancellationToken);
}

/// <summary>Production adapter from the broad CalDAV client to discovery-only acquisition.</summary>
internal sealed class CalendarClientDiscoveryTransport(ICalendarClient client) : ICalendarDiscoveryTransport
{
    public Task<IReadOnlyList<CalendarDescriptor>> DiscoverAsync(CancellationToken cancellationToken) =>
        client.GetCalendarsAsync(cancellationToken);
}

/// <summary>Coordinates one immutable CalDAV discovery acquisition per operation.</summary>
internal sealed class CalendarOperationDiscovery
{
    private readonly ICalendarDiscoveryTransport _transport;
    private readonly Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> _applyScope;
    private readonly Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
        _resolveDefault;
    private Lazy<Task<CalendarOperationDiscoveryResult>>? _acquisition;

    public CalendarOperationDiscovery(
        ICalendarDiscoveryTransport transport,
        Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> applyScope,
        Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
            resolveDefault)
    {
        _transport = transport;
        _applyScope = applyScope;
        _resolveDefault = resolveDefault;
    }

    public Task<CalendarOperationDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(
            ref _acquisition,
            () => new Lazy<Task<CalendarOperationDiscoveryResult>>(
                () => AcquireAsync(cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private async Task<CalendarOperationDiscoveryResult> AcquireAsync(
        CancellationToken cancellationToken)
    {
        var calendars = await _transport.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var scoped = _applyScope(calendars);
        var frozenItems = scoped.Items.Select(Freeze).ToImmutableArray();
        var frozenDiscovery = new CalendarDiscoveryResult(
            frozenItems,
            scoped.Diagnostics.ToImmutableArray());
        return new CalendarOperationDiscoveryResult(
            frozenDiscovery,
            FreezeSelection(_resolveDefault(CalendarEntityKind.Event, calendars, frozenItems)),
            FreezeSelection(_resolveDefault(CalendarEntityKind.Todo, calendars, frozenItems)));
    }

    private static CalendarDescriptor Freeze(CalendarDescriptor calendar) => calendar with
    {
        EventEvidence = calendar.EventEvidence.ToImmutableArray(),
        TodoEvidence = calendar.TodoEvidence.ToImmutableArray(),
        UnavailableProperties = calendar.UnavailableProperties.ToImmutableArray()
    };

    private static CalendarSelectionResult FreezeSelection(CalendarSelectionResult selection) => selection.Code switch
    {
        CalendarSelectionCode.Success => CalendarSelectionResult.Success(selection.Calendar!),
        _ => CalendarSelectionResult.Failure(selection.Code, selection.Candidates.ToImmutableArray())
    };

}

internal sealed record CalendarOperationDiscoveryResult(
    CalendarDiscoveryResult Discovery,
    CalendarSelectionResult EventDefault,
    CalendarSelectionResult TodoDefault)
{
    internal CalendarSelectionResult Default(CalendarEntityKind entityKind) => entityKind switch
    {
        CalendarEntityKind.Event => EventDefault,
        CalendarEntityKind.Todo => TodoDefault,
        _ => CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound)
    };
}
