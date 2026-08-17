using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

internal sealed class CalendarResourceMoveProtocol(
    HttpClient httpClient,
    Uri configuredBaseUri,
    TimeProvider? timeProvider = null)
{
    private const int MaximumRedirects = 3;
    private const int MaximumErrorBodyBytes = 64 * 1024;
    private static readonly HttpMethod MoveMethod = new("MOVE");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<CalendarResourceMoveDispatchResult> MoveAsync(
        CalendarResourceMoveDispatchRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var sourceUri, out var destinationUri, out var entityTag))
            return new CalendarResourceMoveDispatchResult(CalendarResourceMoveDispatchCode.InvalidInput);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await SendAsync(sourceUri, destinationUri, entityTag, cancellationToken);
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
            if (!IsMethodPreservingRedirect(response.StatusCode))
                return await MapResponseAsync(response, cancellationToken);
            if (redirectCount >= MaximumRedirects
                || !TryResolveRedirect(
                    currentSourceUri,
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
        out Uri sourceUri,
        out Uri destinationUri,
        out EntityTagHeaderValue entityTag)
    {
        sourceUri = null!;
        destinationUri = null!;
        entityTag = null!;
        if (!TryValidateEndpoints(request, out sourceUri, out destinationUri)
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
        return TryValidateAbsoluteUri(request.SourceHref, out sourceUri)
            && TryValidateAbsoluteUri(request.DestinationHref, out destinationUri)
            && HasSameOrigin(configuredBaseUri, sourceUri)
            && HasSameOrigin(configuredBaseUri, destinationUri)
            && !string.Equals(sourceUri.AbsoluteUri, destinationUri.AbsoluteUri, StringComparison.Ordinal);
    }

    private static bool TryValidateEntityTag(string value, out EntityTagHeaderValue entityTag)
    {
        entityTag = null!;
        if (!EntityTagHeaderValue.TryParse(value, out var parsedEntityTag)
            || parsedEntityTag is null
            || parsedEntityTag.IsWeak
            || parsedEntityTag == EntityTagHeaderValue.Any
            || !string.Equals(parsedEntityTag.ToString(), value, StringComparison.Ordinal))
        {
            return false;
        }
        entityTag = parsedEntityTag;
        return true;
    }

    private bool TryResolveRedirect(
        Uri currentUri,
        Uri destinationUri,
        Uri? location,
        out Uri redirectUri)
    {
        redirectUri = null!;
        if (location is null
            || location.OriginalString.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(currentUri, location, out var candidate)
            || !TryValidateAbsoluteUri(candidate.AbsoluteUri, out candidate)
            || !HasSameOrigin(configuredBaseUri, candidate)
            || string.Equals(candidate.AbsoluteUri, destinationUri.AbsoluteUri, StringComparison.Ordinal))
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

    private async Task<CalendarResourceMoveDispatchResult> MapResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if ((response.StatusCode is HttpStatusCode.Forbidden
                or HttpStatusCode.Conflict
                or HttpStatusCode.PreconditionFailed)
            && await HasNoUidConflictPreconditionAsync(response.Content, cancellationToken))
        {
            return new CalendarResourceMoveDispatchResult(
                CalendarResourceMoveDispatchCode.DestinationConflict);
        }
        return MapResponse(response);
    }

    private CalendarResourceMoveDispatchResult MapResponse(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent =>
            new(CalendarResourceMoveDispatchCode.Dispatched),
        HttpStatusCode.Accepted => new(CalendarResourceMoveDispatchCode.PossiblyDispatched),
        HttpStatusCode.NotFound => new(CalendarResourceMoveDispatchCode.NotFound),
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed =>
            new(CalendarResourceMoveDispatchCode.Conflict),
        HttpStatusCode.Unauthorized => new(CalendarResourceMoveDispatchCode.UpstreamUnauthorized),
        HttpStatusCode.Forbidden => new(CalendarResourceMoveDispatchCode.UpstreamForbidden),
        HttpStatusCode.RequestEntityTooLarge => new(CalendarResourceMoveDispatchCode.PayloadTooLarge),
        HttpStatusCode.TooManyRequests => new(
            CalendarResourceMoveDispatchCode.UpstreamRateLimited,
            ReadRetryAfterMilliseconds(response.Headers.RetryAfter)),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
            new(CalendarResourceMoveDispatchCode.UnsupportedCapability),
        HttpStatusCode.InsufficientStorage => new(CalendarResourceMoveDispatchCode.UpstreamUnavailable),
        HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError =>
            new(CalendarResourceMoveDispatchCode.PossiblyDispatched),
        _ => new(CalendarResourceMoveDispatchCode.UpstreamProtocolError)
    };

    private static async Task<bool> HasNoUidConflictPreconditionAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumErrorBodyBytes)
            return false;
        try
        {
            await using var source = await content.ReadAsStreamAsync(cancellationToken);
            using var destination = new MemoryStream();
            var buffer = new byte[8192];
            while (destination.Length <= MaximumErrorBodyBytes)
            {
                var remainingPlusOne = (MaximumErrorBodyBytes - (int)destination.Length) + 1;
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remainingPlusOne)),
                    cancellationToken);
                if (read == 0)
                {
                    return DavResponseParser.IsNoUidConflictError(
                        StrictUtf8.GetString(destination.GetBuffer(), 0, (int)destination.Length));
                }
                if (destination.Length + read > MaximumErrorBodyBytes)
                    return false;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is DecoderFallbackException
            or HttpRequestException
            or IOException
            or OperationCanceledException)
        {
            return false;
        }
        return false;
    }

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
