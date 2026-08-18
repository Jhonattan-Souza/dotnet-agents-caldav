using System.Net;
using System.Net.Http.Headers;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

internal sealed class CalendarResourceUpdateProtocol(
    HttpClient httpClient,
    Uri configuredBaseUri,
    TimeProvider? timeProvider = null)
{
    public async Task<CalendarResourceUpdateDispatchResult> UpdateAsync(
        CalendarResourceUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var resourceUri, out var entityTag))
            return new(CalendarResourceUpdateDispatchCode.InvalidInput);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await SendAsync(resourceUri, entityTag, request.AuthoritativeUtf8, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or TimeoutException
            or OperationCanceledException)
        {
            return new(CalendarResourceUpdateDispatchCode.PossiblyDispatched);
        }
    }

    private async Task<CalendarResourceUpdateDispatchResult> SendAsync(
        Uri initialResourceUri,
        EntityTagHeaderValue entityTag,
        ReadOnlyMemory<byte> authoritativeUtf8,
        CancellationToken cancellationToken)
    {
        var currentUri = initialResourceUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Put, currentUri);
            message.Headers.IfMatch.Add(entityTag);
            message.Content = new ByteArrayContent(authoritativeUtf8.ToArray());
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("text/calendar") { CharSet = "utf-8" };
            using var response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!CalendarMutationProtocolPrimitives.IsMethodPreservingRedirect(response.StatusCode))
                return await MapResponseAsync(response, cancellationToken);
            if (redirectCount >= CalendarMutationProtocolPrimitives.MaximumRedirects
                || !TryResolveRedirect(currentUri, response.Headers.Location, out var redirectUri))
            {
                return new(CalendarResourceUpdateDispatchCode.UpstreamProtocolError);
            }
            currentUri = redirectUri;
        }
    }

    private bool TryValidateRequest(
        CalendarResourceUpdateRequest request,
        out Uri resourceUri,
        out EntityTagHeaderValue entityTag)
    {
        resourceUri = null!;
        entityTag = null!;
        if (request.AuthoritativeUtf8.IsEmpty
            || !CalendarMutationProtocolPrimitives.TryValidateAbsoluteUri(request.ResourceHref, out resourceUri)
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

    private async Task<CalendarResourceUpdateDispatchResult> MapResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode >= HttpStatusCode.BadRequest
            && (await DavMutationErrorReader.ReadAsync(response.Content, cancellationToken))
                .HasFlag(DavMutationErrorKind.UnsupportedCapability))
        {
            return new(CalendarResourceUpdateDispatchCode.UnsupportedCapability);
        }
        return MapResponse(response);
    }

    private CalendarResourceUpdateDispatchResult MapResponse(HttpResponseMessage response) =>
        CalendarMutationProtocolPrimitives.Classify(response.StatusCode) switch
    {
        CalendarMutationHttpOutcome.Dispatched =>
            new(CalendarResourceUpdateDispatchCode.Dispatched),
        CalendarMutationHttpOutcome.PossiblyDispatched => new(CalendarResourceUpdateDispatchCode.PossiblyDispatched),
        CalendarMutationHttpOutcome.NotFound => new(CalendarResourceUpdateDispatchCode.NotFound),
        CalendarMutationHttpOutcome.Conflict => new(CalendarResourceUpdateDispatchCode.Conflict),
        CalendarMutationHttpOutcome.UpstreamUnauthorized => new(CalendarResourceUpdateDispatchCode.UpstreamUnauthorized),
        CalendarMutationHttpOutcome.UpstreamForbidden => new(CalendarResourceUpdateDispatchCode.UpstreamForbidden),
        CalendarMutationHttpOutcome.PayloadTooLarge => new(CalendarResourceUpdateDispatchCode.PayloadTooLarge),
        CalendarMutationHttpOutcome.UpstreamRateLimited => new(
            CalendarResourceUpdateDispatchCode.UpstreamRateLimited,
            CalendarMutationProtocolPrimitives.ReadRetryAfterMilliseconds(
                response.Headers.RetryAfter,
                timeProvider ?? TimeProvider.System)),
        CalendarMutationHttpOutcome.UnsupportedCapability =>
            new(CalendarResourceUpdateDispatchCode.UnsupportedCapability),
        CalendarMutationHttpOutcome.UpstreamUnavailable => new(CalendarResourceUpdateDispatchCode.UpstreamUnavailable),
        _ => new(CalendarResourceUpdateDispatchCode.UpstreamProtocolError)
    };
}
