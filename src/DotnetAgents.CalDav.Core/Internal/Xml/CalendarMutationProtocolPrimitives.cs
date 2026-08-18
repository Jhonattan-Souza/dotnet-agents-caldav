using System.Net;
using System.Net.Http.Headers;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>Shared canonical URI, redirect, revision, and HTTP outcome rules for Calendar mutations.</summary>
internal static class CalendarMutationProtocolPrimitives
{
    internal const int MaximumRedirects = 3;

    internal static bool TryValidateAbsoluteUri(string href, out Uri uri)
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

    internal static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    internal static bool TryResolveSameOriginRedirect(
        Uri configuredBaseUri,
        Uri currentUri,
        Uri? location,
        Func<Uri, bool>? additionalValidation,
        out Uri redirectUri)
    {
        redirectUri = null!;
        if (location is null
            || location.OriginalString.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || location.OriginalString.Contains("%2F", StringComparison.OrdinalIgnoreCase)
            || location.OriginalString.Contains("%5C", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(currentUri, location, out var candidate)
            || !IsSafeCanonicalUri(candidate, candidate.AbsoluteUri)
            || !HasSameOrigin(configuredBaseUri, candidate)
            || additionalValidation?.Invoke(candidate) == false)
        {
            return false;
        }
        redirectUri = candidate;
        return true;
    }

    internal static bool TryParseStrongEntityTag(string value, out EntityTagHeaderValue entityTag)
    {
        entityTag = null!;
        if (!EntityTagHeaderValue.TryParse(value, out var parsed)
            || parsed is null
            || parsed.IsWeak
            || parsed == EntityTagHeaderValue.Any
            || !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            return false;
        }
        entityTag = parsed;
        return true;
    }

    internal static bool IsMethodPreservingRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    internal static CalendarMutationHttpOutcome Classify(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent =>
            CalendarMutationHttpOutcome.Dispatched,
        HttpStatusCode.Accepted => CalendarMutationHttpOutcome.PossiblyDispatched,
        >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => CalendarMutationHttpOutcome.OtherSuccess,
        HttpStatusCode.NotFound => CalendarMutationHttpOutcome.NotFound,
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => CalendarMutationHttpOutcome.Conflict,
        HttpStatusCode.Unauthorized => CalendarMutationHttpOutcome.UpstreamUnauthorized,
        HttpStatusCode.Forbidden => CalendarMutationHttpOutcome.UpstreamForbidden,
        HttpStatusCode.RequestEntityTooLarge => CalendarMutationHttpOutcome.PayloadTooLarge,
        HttpStatusCode.TooManyRequests => CalendarMutationHttpOutcome.UpstreamRateLimited,
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
            CalendarMutationHttpOutcome.UnsupportedCapability,
        HttpStatusCode.InsufficientStorage => CalendarMutationHttpOutcome.UpstreamUnavailable,
        HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError =>
            CalendarMutationHttpOutcome.PossiblyDispatched,
        _ => CalendarMutationHttpOutcome.UpstreamProtocolError
    };

    internal static int? ReadRetryAfterMilliseconds(
        RetryConditionHeaderValue? retryAfter,
        TimeProvider timeProvider)
    {
        if (retryAfter is null)
            return null;
        var delay = retryAfter.Delta ?? retryAfter.Date - timeProvider.GetUtcNow();
        if (delay is null)
            return null;
        return (int)Math.Clamp(Math.Ceiling(delay.Value.TotalMilliseconds), 0, int.MaxValue);
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
}

internal enum CalendarMutationHttpOutcome
{
    Dispatched,
    OtherSuccess,
    PossiblyDispatched,
    NotFound,
    Conflict,
    UpstreamUnauthorized,
    UpstreamForbidden,
    PayloadTooLarge,
    UpstreamRateLimited,
    UnsupportedCapability,
    UpstreamUnavailable,
    UpstreamProtocolError
}
