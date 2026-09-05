using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Runs bounded native reports, with one optional-limit negotiation on initial synchronization.</summary>
internal sealed class CalendarReportModule(
    CalDavClient client,
    IOptions<CalDavOptions> options,
    CalendarSyncCheckpointProtector checkpoints) : ICalendarReportModule
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    public async Task<CalendarFreeBusyResult> FreeBusyAsync(CalendarFreeBusyRequest request, CancellationToken cancellationToken)
    {
        ValidateWindow(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var href = await client.AuthorizeProtocolCalendarAsync(request.CalendarHref, deadline.Token).ConfigureAwait(false);
        var body = new XElement(CalDav + "free-busy-query", new XElement(CalDav + "time-range",
            new XAttribute("start", IcalUtc(request.From)), new XAttribute("end", IcalUtc(request.To)))).ToString(SaveOptions.DisableFormatting);
        var response = await client.SendProtocolRequestAsync(href, "REPORT", body, 1, deadline.Token).ConfigureAwait(false);
        EnsureSuccessful(response, href, 200);
        if (!string.Equals(response.ContentType, "text/calendar", StringComparison.OrdinalIgnoreCase))
            throw new CalendarProtocolException("upstream_protocol_error", "The native free/busy report did not return text/calendar.");
        var periods = CalendarFreeBusyReportParser.Parse(response.Body, request.From, request.To, deadline.Token);
        return new(href, CalendarFreeBusyReportParser.FormatUtc(request.From), CalendarFreeBusyReportParser.FormatUtc(request.To), periods);
    }

    public async Task<CalendarResourceChangesResult> ChangesAsync(CalendarResourceChangesRequest request, CancellationToken cancellationToken)
    {
        if (request.PageSize is < 1 or > 500)
            throw new CalendarProtocolException("invalid_input", "pageSize must be between 1 and 500.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var binding = checkpoints.ConfigurationBinding(options.Value);
        var state = await ResolveStateAsync(request, binding, deadline.Token).ConfigureAwait(false);
        var (response, omitLimit) = await SendSyncReportAsync(state, request.PageSize, deadline.Token).ConfigureAwait(false);
        EnsureSuccessful(response, state.CalendarHref, 207);
        var page = CalendarSyncReportParser.Parse(response.Body, state.CalendarHref, state.SyncToken, request.PageSize, deadline.Token);
        if (checkpoints.ConfigurationBinding(options.Value) != binding)
            throw new CalendarProtocolException("sync_reset_required", "The Calendar configuration changed. Start again with calendarHref and no checkpoint.");
        var checkpoint = checkpoints.Protect(state with
        {
            SyncToken = page.SyncToken,
            Initial = state.Initial && page.HasMore,
            OmitLimit = omitLimit
        });
        return new(state.CalendarHref, state.Initial ? "initial" : "incremental", page.Changes, checkpoint, page.HasMore);
    }

    private async Task<(CalendarProtocolResponse Response, bool OmitLimit)> SendSyncReportAsync(
        CalendarSyncCheckpoint state,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var response = await client.SendProtocolRequestAsync(state.CalendarHref, "REPORT",
            BuildSyncReport(state.SyncToken, state.OmitLimit ? null : pageSize), 0, cancellationToken).ConfigureAwait(false);
        if (!CanNegotiateInitialLimit(state, response))
            return (response, state.OmitLimit);

        // Some servers reject the optional limit on initial synchronization. The
        // retry retains the same deadline, byte bound, and accepted entry limit.
        // Keep omission in the authenticated chain: reintroducing a limit into a
        // delta must not silently exchange bounded data for an advanced token.
        var retry = await client.SendProtocolRequestAsync(state.CalendarHref, "REPORT",
            BuildSyncReport(state.SyncToken, null), 0, cancellationToken).ConfigureAwait(false);
        return (retry, true);
    }

    private static bool CanNegotiateInitialLimit(CalendarSyncCheckpoint state, CalendarProtocolResponse response)
    {
        if (state.SyncToken.Length != 0 || state.OmitLimit || response.StatusCode != 507
            || response.RequestHref != state.CalendarHref || response.Body.Length == 0)
            return false;
        try
        {
            var root = CalendarSyncReportParser.ReadXml(response.Body);
            var conditions = root.Elements().Where(element => element.Name.Namespace == Dav).Take(2).ToArray();
            return root.Name == Dav + "error" && conditions.Length == 1
                && IsLimitPrecondition(conditions[0]);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool IsLimitPrecondition(XElement condition) =>
        condition.Name == Dav + "number-of-matches-within-limits"
        && !condition.HasElements && string.IsNullOrWhiteSpace(condition.Value);

    private async Task<CalendarSyncCheckpoint> ResolveStateAsync(
        CalendarResourceChangesRequest request,
        string binding,
        CancellationToken cancellationToken) => request switch
    {
        CalendarResourceChangesRequest.Start start => new(
            await client.AuthorizeProtocolCalendarAsync(start.CalendarHref, cancellationToken).ConfigureAwait(false), string.Empty, true, binding),
        CalendarResourceChangesRequest.Continue next => checkpoints.Unprotect(next.Checkpoint, binding),
        _ => throw new CalendarProtocolException("invalid_input", "Provide calendarHref to start, or checkpoint to continue.")
    };

    private static string BuildSyncReport(string token, int? pageSize) => new XElement(Dav + "sync-collection",
        new XElement(Dav + "sync-token", token), new XElement(Dav + "sync-level", "1"),
        pageSize is { } limit ? new XElement(Dav + "limit", new XElement(Dav + "nresults", limit)) : null,
        new XElement(Dav + "prop", new XElement(Dav + "getetag"))).ToString(SaveOptions.DisableFormatting);

    private static void ValidateWindow(CalendarFreeBusyRequest request)
    {
        if (request.From.Offset != TimeSpan.Zero || request.To.Offset != TimeSpan.Zero
            || request.From.Ticks % TimeSpan.TicksPerSecond != 0 || request.To.Ticks % TimeSpan.TicksPerSecond != 0
            || request.To <= request.From || request.To - request.From > TimeSpan.FromDays(366))
            throw new CalendarProtocolException("invalid_input", "Provide an increasing UTC window of at most 366 days with whole-second boundaries.");
    }

    private static string IcalUtc(DateTimeOffset value) => value.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static void EnsureSuccessful(CalendarProtocolResponse response, string href, int expectedStatus)
    {
        if (response.RequestHref != href)
            throw new CalendarProtocolException("upstream_protocol_error", "The native report responded for an unexpected Calendar href.");
        if (response.StatusCode == expectedStatus)
            return;
        if (response.StatusCode == 507)
            throw new CalendarProtocolException("limit_exhausted", "The server could not satisfy the native report limit. No result or checkpoint was advanced.");
        var condition = ReadErrorCondition(response);
        if (condition is not null)
            throw condition;
        throw new HttpRequestException("The Calendar server rejected the native report.", null, (HttpStatusCode)response.StatusCode);
    }

    private static CalendarProtocolException? ReadErrorCondition(CalendarProtocolResponse response)
    {
        if (response.Body.Length == 0 || response.StatusCode is not (403 or 409))
            return null;
        XElement root;
        try
        {
            root = CalendarSyncReportParser.ReadXml(response.Body);
        }
        catch (XmlException)
        {
            return null;
        }
        if (root.Name != Dav + "error")
            return null;
        if (root.Element(Dav + "valid-sync-token") is not null)
            return new("sync_reset_required", "The server no longer accepts the sync token. Start again with calendarHref and no checkpoint to rebuild the inventory.");
        if (root.Element(Dav + "number-of-matches-within-limits") is not null)
            return new("limit_exhausted", "The server could not satisfy the native report limit. No result or checkpoint was advanced.");
        if (root.Element(Dav + "supported-report") is not null)
            return new("unsupported_capability", "The Calendar server does not support this native report.");
        return null;
    }
}
