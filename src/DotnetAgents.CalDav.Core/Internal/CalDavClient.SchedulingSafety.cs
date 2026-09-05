using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed partial class CalDavClient
{
    public Task<bool> IsStorageOnlyMutationAllowedAsync(
        string calendarHref,
        ReadOnlyMemory<byte> priorUtf8,
        ReadOnlyMemory<byte> proposedUtf8,
        CancellationToken cancellationToken) => CalendarSchedulingSafety.RequiresCheck(priorUtf8, proposedUtf8)
            ? ProveSchedulingAbsentAsync(calendarHref, cancellationToken)
            : Task.FromResult(true);

    private async Task<bool> ProveSchedulingAbsentAsync(string calendarHref, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendProtocolRequestAsync(calendarHref, "OPTIONS", null, null,
                cancellationToken).ConfigureAwait(false);
            return response.StatusCode is >= 200 and <= 299
                && string.Equals(response.RequestHref, calendarHref, StringComparison.Ordinal)
                && CalendarSchedulingSafety.ProvesSchedulingAbsent(response.DavCompliance);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException
            or CalendarProtocolException)
        {
            return false;
        }
    }
}
