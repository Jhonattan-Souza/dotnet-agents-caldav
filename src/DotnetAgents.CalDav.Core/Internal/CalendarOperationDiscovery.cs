using System.Collections.Concurrent;
using System.Collections.Immutable;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;

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

/// <summary>Coordinates one immutable CalDAV discovery acquisition per authorization-bound key and operation.</summary>
internal sealed class CalendarOperationDiscovery
{
    private readonly ICalendarDiscoveryTransport _transport;
    private readonly Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> _applyScope;
    private readonly Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
        _resolveDefault;
    private readonly CalendarDiscoveryKey _key;
    private readonly ConcurrentDictionary<CalendarDiscoveryKey, Lazy<Task<CalendarOperationDiscoveryResult>>> _results = new();

    public CalendarOperationDiscovery(
        ICalendarDiscoveryTransport transport,
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

    public Task<CalendarOperationDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
        GetResultAsync(cancellationToken);

    private async Task<CalendarOperationDiscoveryResult> AcquireAsync(
        CalendarDiscoveryKey key,
        CancellationToken cancellationToken)
    {
        var calendars = await _transport.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var scoped = _applyScope(calendars);
        var frozenItems = scoped.Items.Select(Freeze).ToImmutableArray();
        var frozenDiscovery = new CalendarDiscoveryResult(
            frozenItems,
            scoped.Diagnostics.ToImmutableArray());
        return new CalendarOperationDiscoveryResult(
            key,
            frozenDiscovery,
            FreezeSelection(_resolveDefault(CalendarEntityKind.Event, calendars, frozenItems)),
            FreezeSelection(_resolveDefault(CalendarEntityKind.Todo, calendars, frozenItems)));
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
    CalendarDiscoveryKey Key,
    CalendarDiscoveryResult Discovery,
    CalendarSelectionResult EventDefault,
    CalendarSelectionResult TodoDefault)
{
    internal CalendarOperationDiscoveryResult(
        CalendarDiscoveryResult discovery,
        CalendarSelectionResult eventDefault,
        CalendarSelectionResult todoDefault)
        : this(default, discovery, eventDefault, todoDefault)
    {
    }

    internal CalendarSelectionResult Default(CalendarEntityKind entityKind) => entityKind switch
    {
        CalendarEntityKind.Event => EventDefault,
        CalendarEntityKind.Todo => TodoDefault,
        _ => CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound)
    };
}

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
