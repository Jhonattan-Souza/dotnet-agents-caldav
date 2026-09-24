using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Why an OAuth 2.0 access token could not be obtained.</summary>
internal enum CalDavAuthenticationFailure
{
    /// <summary>The token endpoint rejected the grant or client (HTTP 400 or 401); retrying cannot help.</summary>
    Rejected,

    /// <summary>The token endpoint was unreachable, timed out, or answered with another HTTP status.</summary>
    Unavailable,

    /// <summary>The token endpoint answered successfully with an unusable token response.</summary>
    InvalidResponse
}

/// <summary>
/// A token endpoint failure surfaced through the CalDAV HTTP pipeline. A rejection carries HTTP 401 so
/// every existing mapping reports <c>upstream_unauthorized</c>; other failures carry no status and
/// report <c>upstream_unavailable</c>. Messages are fixed text: they never contain tokens, client
/// secrets, the token endpoint, or any part of its response body.
/// </summary>
internal sealed class CalDavAuthenticationException(CalDavAuthenticationFailure failure, string message)
    : HttpRequestException(message, null, failure == CalDavAuthenticationFailure.Rejected ? HttpStatusCode.Unauthorized : null)
{
    internal CalDavAuthenticationFailure Failure { get; } = failure;
}

/// <summary>
/// Obtains Bearer access tokens with the OAuth 2.0 refresh-token grant (RFC 6749 section 6).
/// The access token lives only in memory, is renewed shortly before <c>expires_in</c> elapses and once
/// after a CalDAV 401, and concurrent callers share a single in-flight token request and its outcome.
/// A rejected grant is remembered for <see cref="RejectionBackoff"/> so that a broken refresh token
/// does not send every CalDAV request to the token endpoint.
/// </summary>
internal sealed class CalDavOAuthCredentialSource : CalDavCredentialSource
{
    internal const string HttpClientName = "DotnetAgents.CalDav.OAuth";
    internal const int MaximumResponseBytes = 64 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumExpirySkew = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RejectionBackoff = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Uri _tokenEndpoint;
    private readonly string _clientId;
    private readonly string? _clientSecret;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string _refreshToken;
    private AccessToken? _current;
    private long _completedRefreshes;
    private CalDavAuthenticationException? _lastFailure;
    private DateTimeOffset _rejectedUntil;

    internal CalDavOAuthCredentialSource(
        CalDavOptions options,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider)
    {
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _tokenEndpoint = new Uri(options.OAuthTokenEndpoint!, UriKind.Absolute);
        _clientId = options.OAuthClientId!;
        _clientSecret = string.IsNullOrEmpty(options.OAuthClientSecret) ? null : options.OAuthClientSecret;
        _refreshToken = options.OAuthRefreshToken!;
    }

    internal override async ValueTask<CalDavCredential> GetAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref _current);
        if (current is not null && current.IsFresh(_timeProvider.GetUtcNow()))
            return current.Credential;
        return await RefreshAsync(rejected: null, cancellationToken).ConfigureAwait(false);
    }

    internal override async ValueTask<CalDavCredential?> RenewAsync(
        CalDavCredential rejected,
        CancellationToken cancellationToken) =>
        await RefreshAsync(rejected, cancellationToken).ConfigureAwait(false);

    private async Task<CalDavCredential> RefreshAsync(CalDavCredential? rejected, CancellationToken cancellationToken)
    {
        var observedRefreshes = Interlocked.Read(ref _completedRefreshes);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A caller that waited here reuses the token its predecessor obtained, unless that is
            // exactly the token the server has just rejected.
            var now = _timeProvider.GetUtcNow();
            var current = _current;
            if (current is not null && current.IsFresh(now) && !ReferenceEquals(current.Credential, rejected))
                return current.Credential;
            // A failure is shared with every caller that queued behind it, and a rejected grant
            // with every caller until the backoff elapses.
            if (_lastFailure is { } failure
                && (observedRefreshes != _completedRefreshes || now < _rejectedUntil))
            {
                throw new CalDavAuthenticationException(failure.Failure, failure.Message);
            }
            return await RequestAndRecordAsync(now, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<CalDavCredential> RequestAndRecordAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var issued = await RequestAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, issued);
            _lastFailure = null;
            return issued.Credential;
        }
        catch (CalDavAuthenticationException exception)
        {
            _lastFailure = exception;
            if (exception.Failure == CalDavAuthenticationFailure.Rejected)
            {
                // Drop the cached token too: it was either rejected by the CalDAV server or is
                // stale, so during the backoff GetAsync fails fast instead of a doomed round trip.
                Volatile.Write(ref _current, null);
                _rejectedUntil = now + RejectionBackoff;
            }
            throw;
        }
        finally
        {
            // Caller cancellation is not an outcome: it records nothing, so waiters retry.
            if (!cancellationToken.IsCancellationRequested)
                Interlocked.Increment(ref _completedRefreshes);
        }
    }

    private async Task<AccessToken> RequestAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var requestedAt = _timeProvider.GetUtcNow();
        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(GrantParameters())
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                throw new CalDavAuthenticationException(
                    CalDavAuthenticationFailure.Rejected,
                    "The OAuth token endpoint rejected the refresh-token grant.");
            if (response.StatusCode != HttpStatusCode.OK)
                throw Unavailable("The OAuth token endpoint returned an unsuccessful HTTP status.");
            var body = await ReadBoundedAsync(response.Content, linked.Token).ConfigureAwait(false);
            return Accept(body, requestedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable("The OAuth token endpoint did not respond within its time limit.");
        }
        catch (Exception exception) when (exception is IOException
            || exception is HttpRequestException and not CalDavAuthenticationException)
        {
            // The inner exception is omitted because transport messages can name the endpoint.
            throw Unavailable("The OAuth token endpoint could not be reached.");
        }
    }

    private IEnumerable<KeyValuePair<string, string>> GrantParameters()
    {
        yield return new("grant_type", "refresh_token");
        yield return new("refresh_token", _refreshToken);
        yield return new("client_id", _clientId);
        if (_clientSecret is not null)
            yield return new("client_secret", _clientSecret);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw InvalidResponse();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
                throw InvalidResponse();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private AccessToken Accept(byte[] body, DateTimeOffset requestedAt)
    {
        TokenResponse response;
        try
        {
            response = TokenResponse.Parse(body);
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
        if (response.RefreshToken is not null)
        {
            // RFC 6749 section 6: a newly issued refresh token replaces the old one. It is kept in
            // memory only, so a restart uses the configured token again.
            _refreshToken = response.RefreshToken;
        }
        return new AccessToken(
            new CalDavCredential("Bearer", response.AccessToken),
            RefreshAfter(requestedAt, response.ExpiresIn));
    }

    private static DateTimeOffset? RefreshAfter(DateTimeOffset requestedAt, long? expiresInSeconds)
    {
        if (expiresInSeconds is not { } seconds)
            return null;
        var lifetime = TimeSpan.FromSeconds(seconds);
        var skew = lifetime / 2 < MaximumExpirySkew ? lifetime / 2 : MaximumExpirySkew;
        return requestedAt + lifetime - skew;
    }

    private static CalDavAuthenticationException Unavailable(string message) =>
        new(CalDavAuthenticationFailure.Unavailable, message);

    private static CalDavAuthenticationException InvalidResponse() =>
        new(CalDavAuthenticationFailure.InvalidResponse, "The OAuth token endpoint returned an unusable token response.");

    private sealed record AccessToken(CalDavCredential Credential, DateTimeOffset? RefreshAfter)
    {
        // Without expires_in the token is used until the CalDAV server rejects it.
        internal bool IsFresh(DateTimeOffset now) => RefreshAfter is not { } deadline || now < deadline;
    }

    private sealed record TokenResponse(string AccessToken, long? ExpiresIn, string? RefreshToken)
    {
        // Records print their members; this one holds an access token and possibly a refresh token.
        public override string ToString() =>
            $"TokenResponse {{ AccessToken = ***, ExpiresIn = {ExpiresIn}, RefreshToken = {(RefreshToken is null ? "none" : "***")} }}";

        internal static TokenResponse Parse(byte[] body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "access_token", out var accessToken)
                || !BearerTokenSyntax.IsValid(accessToken)
                || !TryGetString(root, "token_type", out var tokenType)
                || !string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase)
                || !TryGetExpiresIn(root, out var expiresIn)
                || !TryGetRotatedRefreshToken(root, out var refreshToken))
            {
                throw new JsonException();
            }
            return new TokenResponse(accessToken, expiresIn, refreshToken);
        }

        private static bool TryGetString(JsonElement root, string name, out string value)
        {
            value = string.Empty;
            if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
                return false;
            value = property.GetString()!;
            return true;
        }

        private static bool TryGetExpiresIn(JsonElement root, out long? expiresIn)
        {
            expiresIn = null;
            if (!root.TryGetProperty("expires_in", out var property))
                return true;
            if (property.ValueKind != JsonValueKind.Number
                || !property.TryGetInt64(out var seconds)
                || seconds is <= 0 or > int.MaxValue)
            {
                return false;
            }
            expiresIn = seconds;
            return true;
        }

        private static bool TryGetRotatedRefreshToken(JsonElement root, out string? refreshToken)
        {
            refreshToken = null;
            if (!root.TryGetProperty("refresh_token", out _))
                return true;
            if (!TryGetString(root, "refresh_token", out var value) || value.Length == 0)
                return false;
            refreshToken = value;
            return true;
        }
    }
}
