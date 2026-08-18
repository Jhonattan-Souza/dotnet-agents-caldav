using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

internal sealed class CalendarResourceCreateProtocol(HttpClient httpClient, Uri configuredBaseUri)
{
    private const int MaximumRedirects = 3;
    internal static string BuildResourceHref(string calendarHref, string uid)
    {
        var opaqueName = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(uid)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new Uri(
            new Uri(calendarHref.EndsWith('/') ? calendarHref : calendarHref + '/', UriKind.Absolute),
            opaqueName + ".ics").AbsoluteUri;
    }

    public async Task<CalendarResourceCreateResult> CreateAsync(
        CalendarResourceCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var calendarUri, out var resourceUri))
            return new CalendarResourceCreateResult(CalendarResourceCreateCode.InvalidInput, request.ResourceHref);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await SendAsync(calendarUri, resourceUri, request.AuthoritativeUtf8, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or TimeoutException
            or OperationCanceledException)
        {
            return new CalendarResourceCreateResult(
                CalendarResourceCreateCode.PossiblyDispatched,
                resourceUri.AbsoluteUri);
        }
    }

    private async Task<CalendarResourceCreateResult> SendAsync(
        Uri calendarUri,
        Uri initialResourceUri,
        ReadOnlyMemory<byte> authoritativeUtf8,
        CancellationToken cancellationToken)
    {
        var currentUri = initialResourceUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var message = CreateConditionalPut(currentUri, authoritativeUtf8);
            using var response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!IsMethodPreservingRedirect(response.StatusCode))
                return await MapResponseAsync(response, currentUri.AbsoluteUri, cancellationToken);
            if (redirectCount >= MaximumRedirects
                || !TryResolveRedirect(currentUri, calendarUri, response.Headers.Location, out var redirectUri))
            {
                return new CalendarResourceCreateResult(
                    CalendarResourceCreateCode.UpstreamProtocolError,
                    currentUri.AbsoluteUri);
            }
            currentUri = redirectUri;
        }
    }

    private static HttpRequestMessage CreateConditionalPut(
        Uri resourceUri,
        ReadOnlyMemory<byte> authoritativeUtf8)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, resourceUri)
        {
            Content = new ByteArrayContent(authoritativeUtf8.ToArray())
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("text/calendar")
        {
            CharSet = "utf-8"
        };
        message.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        return message;
    }

    private bool TryValidateRequest(
        CalendarResourceCreateRequest request,
        out Uri calendarUri,
        out Uri resourceUri)
    {
        calendarUri = null!;
        resourceUri = null!;
        return TryValidateAbsoluteUri(request.CalendarHref, out calendarUri)
            && TryValidateAbsoluteUri(request.ResourceHref, out resourceUri)
            && HasSameOrigin(configuredBaseUri, calendarUri)
            && HasSameOrigin(configuredBaseUri, resourceUri)
            && IsDirectResourceOf(calendarUri, resourceUri);
    }

    private static bool TryResolveRedirect(
        Uri currentUri,
        Uri calendarUri,
        Uri? location,
        out Uri redirectUri)
    {
        redirectUri = null!;
        if (location is null
            || location.OriginalString.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || location.OriginalString.Contains("%2F", StringComparison.OrdinalIgnoreCase)
            || location.OriginalString.Contains("%5C", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(currentUri, location, out var candidate)
            || !IsSafeCanonicalUri(candidate, candidate.AbsoluteUri)
            || !HasSameOrigin(currentUri, candidate)
            || !IsDirectResourceOf(calendarUri, candidate))
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

    private static bool IsDirectResourceOf(Uri calendarUri, Uri resourceUri)
    {
        if (!HasSameOrigin(calendarUri, resourceUri))
            return false;
        var calendarPath = calendarUri.AbsolutePath.EndsWith('/')
            ? calendarUri.AbsolutePath
            : calendarUri.AbsolutePath + '/';
        if (!resourceUri.AbsolutePath.StartsWith(calendarPath, StringComparison.Ordinal))
            return false;
        var relativePath = resourceUri.AbsolutePath[calendarPath.Length..];
        return relativePath.Length > 0 && !relativePath.Contains('/');
    }

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static bool IsMethodPreservingRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<CalendarResourceCreateResult> MapResponseAsync(
        HttpResponseMessage response,
        string resourceHref,
        CancellationToken cancellationToken)
    {
        var davError = response.StatusCode >= HttpStatusCode.BadRequest
            ? await DavMutationErrorReader.ReadAsync(response.Content, cancellationToken)
            : DavMutationErrorKind.None;
        if (response.StatusCode == HttpStatusCode.Forbidden
            && davError.HasFlag(DavMutationErrorKind.NoUidConflict))
        {
            return new CalendarResourceCreateResult(CalendarResourceCreateCode.Conflict, resourceHref);
        }
        if (davError.HasFlag(DavMutationErrorKind.UnsupportedCapability))
            return new CalendarResourceCreateResult(CalendarResourceCreateCode.UnsupportedCapability, resourceHref);

        return MapResponse(response.StatusCode, resourceHref);
    }

    private static CalendarResourceCreateResult MapResponse(HttpStatusCode statusCode, string resourceHref) =>
        statusCode switch
        {
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed =>
                new(CalendarResourceCreateCode.Conflict, resourceHref),
            HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
                new(CalendarResourceCreateCode.UnsupportedCapability, resourceHref),
            HttpStatusCode.RequestEntityTooLarge => new(CalendarResourceCreateCode.PayloadTooLarge, resourceHref),
            HttpStatusCode.NotFound => new(CalendarResourceCreateCode.NotFound, resourceHref),
            HttpStatusCode.Unauthorized => new(CalendarResourceCreateCode.UpstreamUnauthorized, resourceHref),
            HttpStatusCode.Forbidden => new(CalendarResourceCreateCode.UpstreamForbidden, resourceHref),
            HttpStatusCode.TooManyRequests => new(CalendarResourceCreateCode.UpstreamRateLimited, resourceHref),
            HttpStatusCode.RequestTimeout => new(CalendarResourceCreateCode.PossiblyDispatched, resourceHref),
            HttpStatusCode.InsufficientStorage =>
                new(CalendarResourceCreateCode.UpstreamUnavailable, resourceHref),
            >= HttpStatusCode.InternalServerError =>
                new(CalendarResourceCreateCode.PossiblyDispatched, resourceHref),
            HttpStatusCode.Accepted => new(CalendarResourceCreateCode.PossiblyDispatched, resourceHref),
            >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices =>
                CalendarResourceCreateResult.Dispatched(resourceHref),
            _ => new(CalendarResourceCreateCode.UpstreamProtocolError, resourceHref)
        };
}
