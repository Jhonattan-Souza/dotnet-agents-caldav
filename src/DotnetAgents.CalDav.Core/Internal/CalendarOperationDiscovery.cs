using System.Collections.Concurrent;
using System.Collections.Immutable;
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
    private readonly ConcurrentDictionary<CalendarDiscoveryKey, Lazy<Task<CalendarOperationDiscoveryResult>>> _results = new();

    public CalendarOperationDiscovery(ICalendarClient transport, IOptions<CalDavOptions> options)
    {
        _transport = transport;
        _options = options;
    }

    public void RediscoverCapabilities() => _transport.RediscoverCapabilities();

    public async Task<IReadOnlyList<CalendarDescriptor>> GetCalendarsAsync(CancellationToken cancellationToken)
    {
        var key = CalendarDiscoveryKey.Create(_options.Value);
        var acquisition = _results.GetOrAdd(
            key,
            _ => new Lazy<Task<CalendarOperationDiscoveryResult>>(
                () => AcquireAsync(key, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return (await acquisition.Value.ConfigureAwait(false)).Calendars;
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
        return new CalendarOperationDiscoveryResult(
            key,
            calendars.Select(Freeze).ToImmutableArray());
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
    ImmutableArray<CalendarDescriptor> Calendars);

internal readonly record struct CalendarDiscoveryKey(
    string Origin,
    string PrincipalIdentity,
    string CalendarHome,
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
            endpoint.AbsoluteUri,
            scope,
            options.DefaultEventCalendarName?.Trim() ?? string.Empty,
            options.DefaultTodoCalendarName?.Trim() ?? string.Empty,
            options.RequestTimeout.Ticks);
    }
}
