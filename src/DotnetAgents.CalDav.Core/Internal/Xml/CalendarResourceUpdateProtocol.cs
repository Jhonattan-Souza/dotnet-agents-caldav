using System.Net;
using System.Net.Http.Headers;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

internal sealed class CalendarResourceUpdateProtocol(
    HttpClient httpClient,
    Uri configuredBaseUri,
    TimeProvider? timeProvider = null)
{
    private const int MaximumRedirects = 3;

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
            if (!IsMethodPreservingRedirect(response.StatusCode))
                return MapResponse(response);
            if (redirectCount >= MaximumRedirects
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
            || !TryValidateAbsoluteUri(request.ResourceHref, out resourceUri)
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
            return false;
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

    private CalendarResourceUpdateDispatchResult MapResponse(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent =>
            new(CalendarResourceUpdateDispatchCode.Dispatched),
        HttpStatusCode.Accepted => new(CalendarResourceUpdateDispatchCode.PossiblyDispatched),
        HttpStatusCode.NotFound => new(CalendarResourceUpdateDispatchCode.NotFound),
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => new(CalendarResourceUpdateDispatchCode.Conflict),
        HttpStatusCode.Unauthorized => new(CalendarResourceUpdateDispatchCode.UpstreamUnauthorized),
        HttpStatusCode.Forbidden => new(CalendarResourceUpdateDispatchCode.UpstreamForbidden),
        HttpStatusCode.RequestEntityTooLarge => new(CalendarResourceUpdateDispatchCode.PayloadTooLarge),
        HttpStatusCode.TooManyRequests => new(
            CalendarResourceUpdateDispatchCode.UpstreamRateLimited,
            ReadRetryAfterMilliseconds(response.Headers.RetryAfter)),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
            new(CalendarResourceUpdateDispatchCode.UnsupportedCapability),
        HttpStatusCode.InsufficientStorage => new(CalendarResourceUpdateDispatchCode.UpstreamUnavailable),
        HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError =>
            new(CalendarResourceUpdateDispatchCode.PossiblyDispatched),
        _ => new(CalendarResourceUpdateDispatchCode.UpstreamProtocolError)
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
