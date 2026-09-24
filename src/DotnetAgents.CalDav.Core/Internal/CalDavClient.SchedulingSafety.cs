using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed partial class CalDavClient
{
    public Task<bool> IsStorageOnlyMutationAllowedAsync(
        string calendarHref,
        ReadOnlyMemory<byte> priorUtf8,
        ReadOnlyMemory<byte> proposedUtf8,
        CancellationToken cancellationToken) => CalendarSchedulingSafety.RequiresCheck(priorUtf8, proposedUtf8)
            ? IsSchedulingPermittedAsync(calendarHref, cancellationToken)
            : Task.FromResult(true);

    /// <summary>
    /// Applies the configured scheduling mode to fresh OPTIONS evidence. A server-managed write on a
    /// server that advertises automatic scheduling is recorded so the outcome can disclose it.
    /// </summary>
    private async Task<bool> IsSchedulingPermittedAsync(string calendarHref, CancellationToken cancellationToken)
    {
        var evidence = await ObserveSchedulingAsync(calendarHref, cancellationToken).ConfigureAwait(false);
        var decision = CalendarSchedulingSafety.Decide(
            CalDavSchedulingModes.IsServerManaged(_options.Value),
            evidence);
        if (decision == CalendarSchedulingDecision.AllowedWithServerScheduling)
            CalendarOperationProgress.SetSchedulingSideEffectsPossible();
        return decision != CalendarSchedulingDecision.Blocked;
    }

    private async Task<CalendarSchedulingEvidence> ObserveSchedulingAsync(
        string calendarHref,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendProtocolRequestAsync(calendarHref, "OPTIONS", null, null,
                cancellationToken).ConfigureAwait(false);
            return response.StatusCode is >= 200 and <= 299
                && string.Equals(response.RequestHref, calendarHref, StringComparison.Ordinal)
                    ? CalendarSchedulingSafety.ReadEvidence(response.DavCompliance)
                    : CalendarSchedulingEvidence.Unknown;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CalendarSchedulingEvidence.Unknown;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException
            or CalendarProtocolException or TimeoutRejectedException or BrokenCircuitException or RateLimiterRejectedException)
        {
            return CalendarSchedulingEvidence.Unknown;
        }
    }
}
