using System.Net;
using System.Net.Http.Headers;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

internal sealed class CalendarResourceMoveProtocol(
    HttpClient httpClient,
    Uri configuredBaseUri,
    TimeProvider? timeProvider = null)
{
    private static readonly HttpMethod MoveMethod = new("MOVE");

    public async Task<CalendarResourceMoveDispatchResult> MoveAsync(
        CalendarResourceMoveDispatchRequest request,
        CancellationToken cancellationToken) => await MoveAsync(
        request,
        ParentHref(request.SourceHref),
        ParentHref(request.DestinationHref),
        cancellationToken);

    public async Task<CalendarResourceMoveDispatchResult> MoveAsync(
        CalendarResourceMoveDispatchRequest request,
        string? authorizedSourceCalendarHref,
        string? authorizedDestinationCalendarHref,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(
                request,
                authorizedSourceCalendarHref,
                authorizedDestinationCalendarHref,
                out var sourceUri,
                out var destinationUri,
                out var sourceCalendarUri,
                out var entityTag))
        {
            return new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.InvalidInput);
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await SendAsync(sourceUri, sourceCalendarUri, destinationUri, entityTag, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or TimeoutException
            or OperationCanceledException)
        {
            return new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.PossiblyDispatched);
        }
    }

    private async Task<CalendarResourceMoveDispatchResult> SendAsync(
        Uri initialSourceUri,
        Uri sourceCalendarUri,
        Uri destinationUri,
        EntityTagHeaderValue entityTag,
        CancellationToken cancellationToken)
    {
        var currentSourceUri = initialSourceUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var message = CreateRequest(currentSourceUri, destinationUri, entityTag);
            using var response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!CalendarMutationProtocolPrimitives.IsMethodPreservingRedirect(response.StatusCode))
                return await MapResponseAsync(response, cancellationToken);
            if (redirectCount >= CalendarMutationProtocolPrimitives.MaximumRedirects
                || !TryResolveRedirect(
                    currentSourceUri,
                    sourceCalendarUri,
                    destinationUri,
                    response.Headers.Location,
                    out var redirectUri))
            {
                return new CalendarResourceMoveDispatchResult(
                    CalendarResourceMoveDispatchCode.UpstreamProtocolError);
            }
            currentSourceUri = redirectUri;
        }
    }

    private static HttpRequestMessage CreateRequest(
        Uri sourceUri,
        Uri destinationUri,
        EntityTagHeaderValue entityTag)
    {
        var message = new HttpRequestMessage(MoveMethod, sourceUri);
        message.Headers.IfMatch.Add(entityTag);
        message.Headers.TryAddWithoutValidation("Destination", destinationUri.AbsoluteUri);
        message.Headers.TryAddWithoutValidation("Overwrite", "F");
        return message;
    }

    private bool TryValidateRequest(
        CalendarResourceMoveDispatchRequest request,
        string? authorizedSourceCalendarHref,
        string? authorizedDestinationCalendarHref,
        out Uri sourceUri,
        out Uri destinationUri,
        out Uri sourceCalendarUri,
        out EntityTagHeaderValue entityTag)
    {
        sourceUri = null!;
        destinationUri = null!;
        sourceCalendarUri = null!;
        entityTag = null!;
        if (!TryValidateEndpoints(request, out sourceUri, out destinationUri)
            || !TryValidateCalendarIdentity(authorizedSourceCalendarHref, sourceUri, out sourceCalendarUri)
            || !TryValidateCalendarIdentity(authorizedDestinationCalendarHref, destinationUri, out _)
            || !TryValidateEntityTag(request.EntityTag, out var parsedEntityTag))
        {
            return false;
        }
        entityTag = parsedEntityTag;
        return true;
    }

    private bool TryValidateEndpoints(
        CalendarResourceMoveDispatchRequest request,
        out Uri sourceUri,
        out Uri destinationUri)
    {
        sourceUri = null!;
        destinationUri = null!;
        return CalendarMutationProtocolPrimitives.TryValidateAbsoluteUri(request.SourceHref, out sourceUri)
            && CalendarMutationProtocolPrimitives.TryValidateAbsoluteUri(request.DestinationHref, out destinationUri)
            && CalendarMutationProtocolPrimitives.HasSameOrigin(configuredBaseUri, sourceUri)
            && CalendarMutationProtocolPrimitives.HasSameOrigin(configuredBaseUri, destinationUri)
            && !string.Equals(sourceUri.AbsoluteUri, destinationUri.AbsoluteUri, StringComparison.Ordinal);
    }

    private static bool TryValidateEntityTag(string value, out EntityTagHeaderValue entityTag)
    {
        entityTag = null!;
        return CalendarMutationProtocolPrimitives.TryParseStrongEntityTag(value, out entityTag);
    }

    private bool TryResolveRedirect(
        Uri currentUri,
        Uri sourceCalendarUri,
        Uri destinationUri,
        Uri? location,
        out Uri redirectUri)
    {
        return CalendarMutationProtocolPrimitives.TryResolveSameOriginRedirect(
            configuredBaseUri,
            currentUri,
            location,
            candidate => IsDirectResourceOf(sourceCalendarUri, candidate)
                && !string.Equals(candidate.AbsoluteUri, destinationUri.AbsoluteUri, StringComparison.Ordinal),
            out redirectUri);
    }

    private bool TryValidateCalendarIdentity(string? calendarHref, Uri resourceUri, out Uri calendarUri)
    {
        calendarUri = null!;
        return calendarHref is not null
            && CalendarMutationProtocolPrimitives.TryValidateAbsoluteUri(calendarHref, out calendarUri)
            && calendarUri.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            && CalendarMutationProtocolPrimitives.HasSameOrigin(configuredBaseUri, calendarUri)
            && IsDirectResourceOf(calendarUri, resourceUri);
    }

    private static bool IsDirectResourceOf(Uri calendarUri, Uri resourceUri) => string.Equals(
        new Uri(resourceUri, ".").AbsoluteUri,
        calendarUri.AbsoluteUri,
        StringComparison.Ordinal);

    private static string? ParentHref(string resourceHref) =>
        Uri.TryCreate(resourceHref, UriKind.Absolute, out var resourceUri)
            ? new Uri(resourceUri, ".").AbsoluteUri
            : null;

    private async Task<CalendarResourceMoveDispatchResult> MapResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var davError = response.StatusCode >= HttpStatusCode.BadRequest
            ? await DavMutationErrorReader.ReadAsync(response.Content, cancellationToken)
            : DavMutationErrorKind.None;
        if ((response.StatusCode is HttpStatusCode.Forbidden
                or HttpStatusCode.Conflict
                or HttpStatusCode.PreconditionFailed)
            && davError.HasFlag(DavMutationErrorKind.NoUidConflict))
        {
            return new CalendarResourceMoveDispatchResult(
                CalendarResourceMoveDispatchCode.Conflict,
                CollisionKind: CalendarResourceMoveDispatchCollisionKind.Uid);
        }
        if (davError.HasFlag(DavMutationErrorKind.UnsupportedCapability))
            return new(CalendarResourceMoveDispatchCode.UnsupportedCapability);
        return MapResponse(response);
    }

    private CalendarResourceMoveDispatchResult MapResponse(HttpResponseMessage response) =>
        CalendarMutationProtocolPrimitives.Classify(response.StatusCode) switch
    {
        CalendarMutationHttpOutcome.Dispatched =>
            new(CalendarResourceMoveDispatchCode.Dispatched),
        CalendarMutationHttpOutcome.PossiblyDispatched => new(CalendarResourceMoveDispatchCode.PossiblyDispatched),
        CalendarMutationHttpOutcome.OtherSuccess => new(CalendarResourceMoveDispatchCode.PossiblyDispatched),
        CalendarMutationHttpOutcome.NotFound => new(CalendarResourceMoveDispatchCode.NotFound),
        CalendarMutationHttpOutcome.Conflict =>
            new(
                CalendarResourceMoveDispatchCode.Conflict,
                CollisionKind: CalendarResourceMoveDispatchCollisionKind.Unclassified),
        CalendarMutationHttpOutcome.UpstreamUnauthorized => new(CalendarResourceMoveDispatchCode.UpstreamUnauthorized),
        CalendarMutationHttpOutcome.UpstreamForbidden => new(CalendarResourceMoveDispatchCode.UpstreamForbidden),
        CalendarMutationHttpOutcome.PayloadTooLarge => new(CalendarResourceMoveDispatchCode.PayloadTooLarge),
        CalendarMutationHttpOutcome.UpstreamRateLimited => new(
            CalendarResourceMoveDispatchCode.UpstreamRateLimited,
            CalendarMutationProtocolPrimitives.ReadRetryAfterMilliseconds(
                response.Headers.RetryAfter,
                timeProvider ?? TimeProvider.System)),
        CalendarMutationHttpOutcome.UnsupportedCapability =>
            new(CalendarResourceMoveDispatchCode.UnsupportedCapability),
        CalendarMutationHttpOutcome.UpstreamUnavailable => new(CalendarResourceMoveDispatchCode.UpstreamUnavailable),
        _ => new(CalendarResourceMoveDispatchCode.UpstreamProtocolError)
    };
}
