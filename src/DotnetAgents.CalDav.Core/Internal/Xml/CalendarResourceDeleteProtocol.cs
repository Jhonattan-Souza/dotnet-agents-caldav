using System.Net;
using System.Net.Http.Headers;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

internal sealed class CalendarResourceDeleteProtocol(
    HttpClient httpClient,
    Uri configuredBaseUri,
    TimeProvider? timeProvider = null)
{
    private const int MaximumRedirects = 3;

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
            if (!IsMethodPreservingRedirect(response.StatusCode))
                return await MapResponseAsync(response, cancellationToken);
            if (redirectCount >= MaximumRedirects
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
        if (!TryValidateAbsoluteUri(request.ResourceHref, out resourceUri)
            || !HasSameOrigin(configuredBaseUri, resourceUri)
            || !EntityTagHeaderValue.TryParse(request.EntityTag, out var parsedEntityTag)
            || parsedEntityTag is null
            || parsedEntityTag.IsWeak
            || parsedEntityTag == EntityTagHeaderValue.Any
            || !string.Equals(parsedEntityTag.ToString(), request.EntityTag, StringComparison.Ordinal))
        {
            return false;
        }
        entityTag = parsedEntityTag;
        return true;
    }

    private bool TryResolveRedirect(Uri currentUri, Uri? location, out Uri redirectUri)
    {
        redirectUri = null!;
        if (location is null
            || location.OriginalString.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || location.OriginalString.Contains("%2F", StringComparison.OrdinalIgnoreCase)
            || location.OriginalString.Contains("%5C", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(currentUri, location, out var candidate)
            || !IsSafeCanonicalUri(candidate, candidate.AbsoluteUri)
            || !HasSameOrigin(configuredBaseUri, candidate))
        {
            return false;
        }
        redirectUri = candidate;
        return true;
    }

    private static bool TryValidateAbsoluteUri(string href, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var candidate)
            || !IsSafeCanonicalUri(candidate, href))
        {
            return false;
        }
        uri = candidate;
        return true;
    }

    private static bool IsSafeCanonicalUri(Uri candidate, string original) =>
        (candidate.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        && string.IsNullOrEmpty(candidate.UserInfo)
        && string.IsNullOrEmpty(candidate.Fragment)
        && string.IsNullOrEmpty(candidate.Query)
        && !candidate.AbsolutePath.Contains("%2F", StringComparison.OrdinalIgnoreCase)
        && !candidate.AbsolutePath.Contains("%5C", StringComparison.OrdinalIgnoreCase)
        && !original.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.AbsoluteUri, original, StringComparison.Ordinal);

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static bool IsMethodPreservingRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

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

    private CalendarResourceDeleteDispatchResult MapResponse(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Accepted => new(CalendarResourceDeleteDispatchCode.PossiblyDispatched),
        >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices =>
            new(CalendarResourceDeleteDispatchCode.Dispatched),
        HttpStatusCode.NotFound => new(CalendarResourceDeleteDispatchCode.NotFound),
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed =>
            new(CalendarResourceDeleteDispatchCode.Conflict),
        HttpStatusCode.Unauthorized => new(CalendarResourceDeleteDispatchCode.UpstreamUnauthorized),
        HttpStatusCode.Forbidden => new(CalendarResourceDeleteDispatchCode.UpstreamForbidden),
        HttpStatusCode.RequestEntityTooLarge => new(CalendarResourceDeleteDispatchCode.PayloadTooLarge),
        HttpStatusCode.TooManyRequests => new(
            CalendarResourceDeleteDispatchCode.UpstreamRateLimited,
            ReadRetryAfterMilliseconds(response.Headers.RetryAfter)),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
            new(CalendarResourceDeleteDispatchCode.UnsupportedCapability),
        HttpStatusCode.InsufficientStorage => new(CalendarResourceDeleteDispatchCode.UpstreamUnavailable),
        HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError =>
            new(CalendarResourceDeleteDispatchCode.PossiblyDispatched),
        _ => new(CalendarResourceDeleteDispatchCode.UpstreamProtocolError)
    };

    private int? ReadRetryAfterMilliseconds(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null)
            return null;
        var delay = retryAfter.Delta ?? retryAfter.Date - (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (delay is null)
            return null;
        return (int)Math.Clamp(Math.Ceiling(delay.Value.TotalMilliseconds), 0, int.MaxValue);
    }
}
