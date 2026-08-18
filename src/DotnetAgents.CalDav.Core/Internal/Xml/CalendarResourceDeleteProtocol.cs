using System.Net;
using System.Net.Http.Headers;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

internal sealed class CalendarResourceDeleteProtocol(
    HttpClient httpClient,
    Uri configuredBaseUri,
    TimeProvider? timeProvider = null)
{
    public async Task<CalendarResourceDeleteDispatchResult> DeleteAsync(
        CalendarResourceDeleteRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var resourceUri, out var entityTag))
            return new CalendarResourceDeleteDispatchResult(CalendarResourceDeleteDispatchCode.InvalidInput);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await SendAsync(resourceUri, entityTag, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or TimeoutException
            or OperationCanceledException)
        {
            return new CalendarResourceDeleteDispatchResult(CalendarResourceDeleteDispatchCode.PossiblyDispatched);
        }
    }

    private async Task<CalendarResourceDeleteDispatchResult> SendAsync(
        Uri initialResourceUri,
        EntityTagHeaderValue entityTag,
        CancellationToken cancellationToken)
    {
        var currentUri = initialResourceUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Delete, currentUri);
            message.Headers.IfMatch.Add(entityTag);
            using var response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!CalendarMutationProtocolPrimitives.IsMethodPreservingRedirect(response.StatusCode))
                return await MapResponseAsync(response, cancellationToken);
            if (redirectCount >= CalendarMutationProtocolPrimitives.MaximumRedirects
                || !TryResolveRedirect(currentUri, response.Headers.Location, out var redirectUri))
            {
                return new CalendarResourceDeleteDispatchResult(
                    CalendarResourceDeleteDispatchCode.UpstreamProtocolError);
            }
            currentUri = redirectUri;
        }
    }

    private bool TryValidateRequest(
        CalendarResourceDeleteRequest request,
        out Uri resourceUri,
        out EntityTagHeaderValue entityTag)
    {
        resourceUri = null!;
        entityTag = null!;
        if (!CalendarMutationProtocolPrimitives.TryValidateAbsoluteUri(request.ResourceHref, out resourceUri)
            || !CalendarMutationProtocolPrimitives.HasSameOrigin(configuredBaseUri, resourceUri)
            || !CalendarMutationProtocolPrimitives.TryParseStrongEntityTag(request.EntityTag, out var parsedEntityTag))
        {
            return false;
        }
        entityTag = parsedEntityTag;
        return true;
    }

    private bool TryResolveRedirect(Uri currentUri, Uri? location, out Uri redirectUri)
    {
        return CalendarMutationProtocolPrimitives.TryResolveSameOriginRedirect(
            configuredBaseUri,
            currentUri,
            location,
            additionalValidation: null,
            out redirectUri);
    }

    private async Task<CalendarResourceDeleteDispatchResult> MapResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode >= HttpStatusCode.BadRequest
            && (await DavMutationErrorReader.ReadAsync(response.Content, cancellationToken))
                .HasFlag(DavMutationErrorKind.UnsupportedCapability))
        {
            return new(CalendarResourceDeleteDispatchCode.UnsupportedCapability);
        }
        return MapResponse(response);
    }

    private CalendarResourceDeleteDispatchResult MapResponse(HttpResponseMessage response) =>
        CalendarMutationProtocolPrimitives.Classify(response.StatusCode) switch
    {
        CalendarMutationHttpOutcome.PossiblyDispatched => new(CalendarResourceDeleteDispatchCode.PossiblyDispatched),
        CalendarMutationHttpOutcome.Dispatched or CalendarMutationHttpOutcome.OtherSuccess =>
            new(CalendarResourceDeleteDispatchCode.Dispatched),
        CalendarMutationHttpOutcome.NotFound => new(CalendarResourceDeleteDispatchCode.NotFound),
        CalendarMutationHttpOutcome.Conflict =>
            new(CalendarResourceDeleteDispatchCode.Conflict),
        CalendarMutationHttpOutcome.UpstreamUnauthorized => new(CalendarResourceDeleteDispatchCode.UpstreamUnauthorized),
        CalendarMutationHttpOutcome.UpstreamForbidden => new(CalendarResourceDeleteDispatchCode.UpstreamForbidden),
        CalendarMutationHttpOutcome.PayloadTooLarge => new(CalendarResourceDeleteDispatchCode.PayloadTooLarge),
        CalendarMutationHttpOutcome.UpstreamRateLimited => new(
            CalendarResourceDeleteDispatchCode.UpstreamRateLimited,
            CalendarMutationProtocolPrimitives.ReadRetryAfterMilliseconds(
                response.Headers.RetryAfter,
                timeProvider ?? TimeProvider.System)),
        CalendarMutationHttpOutcome.UnsupportedCapability =>
            new(CalendarResourceDeleteDispatchCode.UnsupportedCapability),
        CalendarMutationHttpOutcome.UpstreamUnavailable => new(CalendarResourceDeleteDispatchCode.UpstreamUnavailable),
        _ => new(CalendarResourceDeleteDispatchCode.UpstreamProtocolError)
    };
}
