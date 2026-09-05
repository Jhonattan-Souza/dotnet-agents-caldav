using System.Text;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarMetadataModule(CalDavClient client) : ICalendarMetadataModule
{
    public async Task<CalendarMetadataSnapshot> InspectAsync(string calendarHref, CancellationToken cancellationToken)
    {
        var href = await client.AuthorizeProtocolCalendarAsync(calendarHref, cancellationToken).ConfigureAwait(false);
        var observed = await ReadAsync(href, cancellationToken).ConfigureAwait(false);
        var scheduling = await ReadSchedulingAsync(href, cancellationToken).ConfigureAwait(false);
        return observed.Snapshot with { Scheduling = scheduling };
    }

    public async Task<CalendarMetadataPatchResult> PatchAsync(
        string calendarHref,
        CalendarMetadataPatch patch,
        CancellationToken cancellationToken)
    {
        CalendarMetadataPatchProtocol.Validate(patch);
        var href = await client.AuthorizeProtocolCalendarAsync(calendarHref, cancellationToken).ConfigureAwait(false);
        // Require the target's Calendar resource type before a metadata write, even with an
        // explicit href allowlist. This read does not act as a concurrency precondition.
        await ReadAsync(href, cancellationToken).ConfigureAwait(false);
        var body = CalendarMetadataPatchProtocol.Body(patch);
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Fetch);
        var dispatch = await DispatchAsync(href, patch, body, cancellationToken).ConfigureAwait(false);
        if (dispatch.State == CalendarMutationState.NotCommitted)
            return new(dispatch.State, Error: dispatch.Error);
        return await ReconcileAsync(href, patch, dispatch, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CalendarMetadataPatchDispatch> DispatchAsync(
        string href,
        CalendarMetadataPatch patch,
        string body,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.SendProtocolRequestAsync(href, "PROPPATCH", body, null, cancellationToken).ConfigureAwait(false);
            return CalendarMetadataPatchProtocol.ReadDispatch(href, patch, response);
        }
        catch (Exception exception) when (IsUncertainFailure(exception))
        {
            return CalendarMetadataPatchProtocol.Uncertain();
        }
    }

    private async Task<CalendarMetadataPatchResult> ReconcileAsync(
        string href,
        CalendarMetadataPatch patch,
        CalendarMetadataPatchDispatch dispatch,
        CancellationToken cancellationToken)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Reconcile);
        CalendarMetadataObservation observed;
        try
        {
            observed = await ReadAsync(href, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUncertainFailure(exception))
        {
            return ReconcileObservation(patch, dispatch, null);
        }
        return ReconcileObservation(patch, dispatch, observed);
    }

    private static CalendarMetadataPatchResult ReconcileObservation(
        CalendarMetadataPatch patch,
        CalendarMetadataPatchDispatch dispatch,
        CalendarMetadataObservation? observed)
    {
        if (dispatch.State == CalendarMutationState.Unknown)
            return new(dispatch.State, observed?.Snapshot, dispatch.Error);
        if (observed is null || !CalendarMetadataPatchProtocol.Matches(patch, observed))
            return new(CalendarMutationState.Committed, observed?.Snapshot,
                new CalendarProtocolException("committed_but_unverified", "The server acknowledged the property update, but readback did not verify the requested values. Inspect the Calendar before another write."));
        return new(CalendarMutationState.Committed, observed.Snapshot);
    }

    private async Task<CalendarMetadataObservation> ReadAsync(string href, CancellationToken cancellationToken)
    {
        var response = await client.SendProtocolRequestAsync(href, "PROPFIND",
            CalendarMetadataProtocol.InspectBody(), 0, cancellationToken).ConfigureAwait(false);
        try
        {
            return CalendarMetadataProtocol.ParseMetadata(href, response, cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw CalendarMetadataProtocol.ProtocolError();
        }
    }

    private async Task<CalendarSchedulingObservation> ReadSchedulingAsync(string href, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.SendProtocolRequestAsync(href, "OPTIONS", null, null, cancellationToken).ConfigureAwait(false);
            return SchedulingObservation(response);
        }
        catch (Exception exception) when (IsOptionalOptionsFailure(exception, cancellationToken))
        {
            return new("unknown", null);
        }
    }

    private static CalendarSchedulingObservation SchedulingObservation(CalendarProtocolResponse response)
    {
        var tokens = response.DavCompliance.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries)).ToArray();
        if (response.StatusCode is < 200 or >= 300)
            return new("unknown", response.StatusCode);
        if (tokens.Contains("calendar-auto-schedule", StringComparer.OrdinalIgnoreCase))
            return new("advertised", response.StatusCode);
        return new(CalendarSchedulingSafety.ProvesSchedulingAbsent(response.DavCompliance)
            ? "not_advertised" : "unknown", response.StatusCode);
    }

    private static bool IsOptionalOptionsFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException or TimeoutException or CalendarProtocolException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private static bool IsUncertainFailure(Exception exception) => exception is HttpRequestException or IOException
        or TimeoutException or OperationCanceledException or CalendarProtocolException or XmlException;
}
