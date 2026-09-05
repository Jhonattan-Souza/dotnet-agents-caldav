using System.Net;
using System.Text;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed partial class CalDavClient
{
    internal async Task<CalendarCollectionDiscoverySnapshot> DiscoverCalendarCollectionsAsync(
        CancellationToken cancellationToken)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Discovery);
        _logger.LogDebug("CalDAV operation {Code} entered {Phase}", "calendar_discovery", "selectionDiscoveryCapability");
        var budget = new CalendarTraversalBudget();
        var homes = await DiscoverCalendarHomesAsync(budget, cancellationToken).ConfigureAwait(false);
        var calendars = new Dictionary<string, CalendarDescriptor>(StringComparer.Ordinal);
        var pending = new Queue<(string Href, int Depth)>(homes.Select(home => (home, 0)));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var scope = CalendarDiscoveryPolicy.ParseScope(_options.Value.CalendarHrefs);
        while (pending.TryDequeue(out var collection))
        {
            if (!CanContainScopedCalendar(collection.Href, scope) || !visited.Add(collection.Href))
                continue;
            var response = await ReadDiscoveryAsync(collection.Href,
                DavRequestBuilder.BuildPropFindCalendarProperties(), 1, budget, cancellationToken).ConfigureAwait(false);
            AddDiscoveredMembers(response, collection.Depth, calendars, pending, scope);
        }
        return new(homes[0], calendars.Values.OrderBy(item => item.Href, StringComparer.Ordinal).ToArray())
        {
            HomeSetHrefs = homes
        };
    }

    private void AddDiscoveredMembers(
        (string Content, Uri RequestUri) response,
        int depth,
        Dictionary<string, CalendarDescriptor> calendars,
        Queue<(string Href, int Depth)> pending,
        IReadOnlyList<string> scope)
    {
        foreach (var member in DavResponseParser.ParseCollectionMembers(response.Content))
        {
            if (!TryCanonicalizeCalendarHref(response.RequestUri, member.Href, out var canonical))
                throw new CalendarDiscoveryProtocolException("Unsafe discovered collection href.");
            ValidateDiscoveryMember(response.RequestUri, canonical);
            if (member.Calendar is { } calendar)
            {
                calendars[canonical] = calendar with { Href = canonical };
                if (calendars.Count > 256)
                    throw new CalendarDiscoveryLimitException(calendars.Count);
                continue;
            }
            QueueChildCollection(response.RequestUri, canonical, depth, pending, scope);
        }
    }

    private static void QueueChildCollection(
        Uri parent, string href, int depth, Queue<(string Href, int Depth)> pending, IReadOnlyList<string> scope)
    {
        if (IsCollectionSelfHref(parent, href) || !CanContainScopedCalendar(href, scope))
            return;
        if (depth >= 8)
            throw new CalendarDiscoveryLimitException("depth", depth + 1, 8);
        pending.Enqueue((href, depth + 1));
    }

    private static bool CanContainScopedCalendar(string href, IReadOnlyList<string> scope)
    {
        if (scope.Count == 0)
            return true;
        var prefix = href.TrimEnd('/') + '/';
        return scope.Any(calendar => string.Equals(calendar.TrimEnd('/'), href.TrimEnd('/'), StringComparison.Ordinal)
            || calendar.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static void ValidateDiscoveryMember(Uri parent, string href)
    {
        if (IsCollectionSelfHref(parent, href))
            return;
        var child = new Uri(href, UriKind.Absolute);
        var prefix = parent.AbsolutePath.TrimEnd('/') + '/';
        var relative = child.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
            ? child.AbsolutePath[prefix.Length..].TrimEnd('/') : string.Empty;
        if (relative.Length == 0 || relative.Contains('/'))
            throw new CalendarDiscoveryProtocolException("A discovery member escaped its direct parent collection.");
    }

    private async Task<IReadOnlyList<string>> DiscoverCalendarHomesAsync(
        CalendarTraversalBudget budget, CancellationToken cancellationToken)
    {
        var configured = await ProbeDiscoveryAsync(_options.Value.BaseUrl.TrimEnd('/') + "/",
            budget, true, cancellationToken).ConfigureAwait(false);
        if (configured.Homes.Any(IsConfiguredCalendarHome))
            return configured.Homes;
        var wellKnown = await ProbeDiscoveryAsync("/.well-known/caldav",
            budget, false, cancellationToken).ConfigureAwait(false);
        if (wellKnown.Homes.Count > 0)
            return wellKnown.Homes;
        if (wellKnown.Principal is null && configured.Homes.Count > 0)
            return configured.Homes;
        var principal = wellKnown.Principal ?? configured.Principal;
        if (principal is null)
            throw new CalendarDiscoveryProtocolException("Calendar-home-set was not discovered.");
        var discovered = await ProbeDiscoveryAsync(principal, budget, false, cancellationToken).ConfigureAwait(false);
        return discovered.Homes.Count > 0 ? discovered.Homes
            : throw new CalendarDiscoveryProtocolException("Calendar-home-set was not discovered.");
    }

    private bool IsConfiguredCalendarHome(string home)
    {
        var configured = new Uri(_options.Value.BaseUrl, UriKind.Absolute);
        return string.Equals(configured.AbsolutePath.TrimEnd('/'),
            new Uri(home, UriKind.Absolute).AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    private async Task<CalendarHomeDiscovery> ProbeDiscoveryAsync(
        string href, CalendarTraversalBudget budget, bool allowUnsupported, CancellationToken cancellationToken)
    {
        try
        {
            var response = await ReadDiscoveryAsync(href, DavRequestBuilder.BuildPropFindCalendarHomeSet(),
                0, budget, cancellationToken).ConfigureAwait(false);
            return ParseHomeDiscovery(response);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound
            || allowUnsupported && exception.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            return new([], null);
        }
    }

    private CalendarHomeDiscovery ParseHomeDiscovery((string Content, Uri RequestUri) response)
    {
        var homes = DavResponseParser.ParseCalendarHomeSets(response.Content)
            .Select(href => CanonicalDiscoveryHref(response.RequestUri, href))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (homes.Length > 16)
            throw new CalendarDiscoveryLimitException("home_count", homes.Length, 16);
        var principal = DavResponseParser.ParseCurrentUserPrincipal(response.Content);
        return new(homes, principal is null ? null : CanonicalDiscoveryHref(response.RequestUri, principal));
    }

    private string CanonicalDiscoveryHref(Uri requestUri, string href) =>
        TryCanonicalizeCalendarHref(requestUri, href, out var canonical) ? canonical
            : throw new CalendarDiscoveryProtocolException("Unsafe Calendar discovery href.");

    private async Task<(string Content, Uri RequestUri)> ReadDiscoveryAsync(
        string href, string body, int depth, CalendarTraversalBudget budget, CancellationToken cancellationToken)
    {
        budget.BeforeRequest();
        var response = await SendPropFindAsync(href, body, depth, cancellationToken).ConfigureAwait(false);
        budget.AddBytes(Encoding.UTF8.GetByteCount(response.Content));
        return response;
    }

    private sealed record CalendarHomeDiscovery(IReadOnlyList<string> Homes, string? Principal);

    private sealed class CalendarTraversalBudget
    {
        private int _requests;
        private long _bytes;

        internal void BeforeRequest()
        {
            if (++_requests > 64)
                throw new CalendarDiscoveryLimitException("request_count", _requests, 64);
        }

        internal void AddBytes(int bytes)
        {
            _bytes += bytes;
            if (_bytes > 16 * 1024 * 1024)
                throw new CalendarDiscoveryLimitException("byte_count", _bytes, 16 * 1024 * 1024);
        }
    }
}
