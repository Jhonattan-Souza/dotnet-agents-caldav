using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

internal sealed class CalendarResourceCreateProtocol(HttpClient httpClient, Uri configuredBaseUri)
{
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
            if (!CalendarMutationProtocolPrimitives.IsMethodPreservingRedirect(response.StatusCode))
                return await MapResponseAsync(response, currentUri.AbsoluteUri, cancellationToken);
            if (redirectCount >= CalendarMutationProtocolPrimitives.MaximumRedirects
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
        return CalendarMutationProtocolPrimitives.TryValidateAbsoluteUri(request.CalendarHref, out calendarUri)
            && CalendarMutationProtocolPrimitives.TryValidateAbsoluteUri(request.ResourceHref, out resourceUri)
            && CalendarMutationProtocolPrimitives.HasSameOrigin(configuredBaseUri, calendarUri)
            && CalendarMutationProtocolPrimitives.HasSameOrigin(configuredBaseUri, resourceUri)
            && IsDirectResourceOf(calendarUri, resourceUri);
    }

    private static bool TryResolveRedirect(
        Uri currentUri,
        Uri calendarUri,
        Uri? location,
        out Uri redirectUri)
    {
        return CalendarMutationProtocolPrimitives.TryResolveSameOriginRedirect(
            currentUri,
            currentUri,
            location,
            candidate => IsDirectResourceOf(calendarUri, candidate),
            out redirectUri);
    }

    private static bool IsDirectResourceOf(Uri calendarUri, Uri resourceUri)
    {
        if (!CalendarMutationProtocolPrimitives.HasSameOrigin(calendarUri, resourceUri))
            return false;
        var calendarPath = calendarUri.AbsolutePath.EndsWith('/')
            ? calendarUri.AbsolutePath
            : calendarUri.AbsolutePath + '/';
        if (!resourceUri.AbsolutePath.StartsWith(calendarPath, StringComparison.Ordinal))
            return false;
        var relativePath = resourceUri.AbsolutePath[calendarPath.Length..];
        return relativePath.Length > 0 && !relativePath.Contains('/');
    }

    private static async Task<CalendarResourceCreateResult> MapResponseAsync(
        HttpResponseMessage response,
        string resourceHref,
        CancellationToken cancellationToken)
    {
        var davError = response.StatusCode >= HttpStatusCode.BadRequest
            ? await DavMutationErrorReader.ReadAsync(response.Content, cancellationToken)
            : DavMutationErrorKind.None;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict
            && davError.HasFlag(DavMutationErrorKind.NoUidConflict))
        {
            return new CalendarResourceCreateResult(CalendarResourceCreateCode.UidConflict, resourceHref);
        }
        if (davError.HasFlag(DavMutationErrorKind.UnsupportedCapability))
            return new CalendarResourceCreateResult(CalendarResourceCreateCode.UnsupportedCapability, resourceHref);

        return response.StatusCode == HttpStatusCode.PreconditionFailed
            ? new CalendarResourceCreateResult(CalendarResourceCreateCode.DestinationConflict, resourceHref)
            : MapResponse(response.StatusCode, resourceHref);
    }

    private static CalendarResourceCreateResult MapResponse(HttpStatusCode statusCode, string resourceHref) =>
        CalendarMutationProtocolPrimitives.Classify(statusCode) switch
        {
            CalendarMutationHttpOutcome.Conflict =>
                new(CalendarResourceCreateCode.Conflict, resourceHref),
            CalendarMutationHttpOutcome.UnsupportedCapability =>
                new(CalendarResourceCreateCode.UnsupportedCapability, resourceHref),
            CalendarMutationHttpOutcome.PayloadTooLarge => new(CalendarResourceCreateCode.PayloadTooLarge, resourceHref),
            CalendarMutationHttpOutcome.NotFound => new(CalendarResourceCreateCode.NotFound, resourceHref),
            CalendarMutationHttpOutcome.UpstreamUnauthorized => new(CalendarResourceCreateCode.UpstreamUnauthorized, resourceHref),
            CalendarMutationHttpOutcome.UpstreamForbidden => new(CalendarResourceCreateCode.UpstreamForbidden, resourceHref),
            CalendarMutationHttpOutcome.UpstreamRateLimited => new(CalendarResourceCreateCode.UpstreamRateLimited, resourceHref),
            CalendarMutationHttpOutcome.UpstreamUnavailable =>
                new(CalendarResourceCreateCode.UpstreamUnavailable, resourceHref),
            CalendarMutationHttpOutcome.PossiblyDispatched =>
                new(CalendarResourceCreateCode.PossiblyDispatched, resourceHref),
            CalendarMutationHttpOutcome.Dispatched or CalendarMutationHttpOutcome.OtherSuccess =>
                CalendarResourceCreateResult.Dispatched(resourceHref),
            _ => new(CalendarResourceCreateCode.UpstreamProtocolError, resourceHref)
        };
}
