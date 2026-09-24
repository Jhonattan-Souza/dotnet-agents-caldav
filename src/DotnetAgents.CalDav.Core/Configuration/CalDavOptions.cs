using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Configuration;

/// <summary>
/// Configuration options for the CalDAV client.
/// Bound from configuration via <c>AddCalDavCalendars</c> DI extension.
/// </summary>
public sealed class CalDavOptions
{
    public const string SectionName = "CalDav";

    /// <summary>Absolute CalDAV server endpoint or Calendar Home URL.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// HTTP authentication scheme from <see cref="CalDavAuthenticationSchemes"/>.
    /// Null or empty selects <see cref="CalDavAuthenticationSchemes.Basic"/>.
    /// </summary>
    public string? AuthenticationScheme { get; set; }

    /// <summary>Username for Basic authentication. Must be empty for Bearer and OAuth 2.0 authentication.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Password for Basic authentication, or the static token for Bearer authentication.
    /// Must be empty for OAuth 2.0 authentication.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Absolute HTTPS OAuth 2.0 token endpoint used for the refresh-token grant.</summary>
    public string? OAuthTokenEndpoint { get; set; }

    /// <summary>OAuth 2.0 client identifier sent with the refresh-token grant.</summary>
    public string? OAuthClientId { get; set; }

    /// <summary>Optional OAuth 2.0 client secret sent with the refresh-token grant.</summary>
    public string? OAuthClientSecret { get; set; }

    /// <summary>OAuth 2.0 refresh token exchanged for short-lived access tokens.</summary>
    public string? OAuthRefreshToken { get; set; }

    /// <summary>Comma-separated exact canonical Calendar href allowlist. Empty means every discovered Calendar.</summary>
    public string? CalendarHrefs { get; set; }

    /// <summary>Display name of the default Calendar for To-do operations.</summary>
    public string? DefaultTodoCalendarName { get; set; }

    /// <summary>Display name of the default Calendar for Event operations.</summary>
    public string? DefaultEventCalendarName { get; set; }

    /// <summary>Optional explicit IANA zone for temporal query evaluation.</summary>
    public string? EvaluationTimeZone { get; set; }

    /// <summary>Explicit server runtime whose atomic mutation preconditions were verified.</summary>
    public string? InteroperabilityProfile { get; set; }

    /// <summary>
    /// Scheduling model for participation-bearing writes and collection deletion. Unset means
    /// <see cref="CalDavSchedulingModes.StorageOnly"/>.
    /// </summary>
    public string? SchedulingMode { get; set; }

    /// <summary>
    /// Optional comma-separated HTTPS host allowlist for cross-origin redirects and discovered hrefs,
    /// such as <c>p01-caldav.icloud.com</c> or the strict-subdomain suffix <c>.icloud.com</c>.
    /// Empty keeps every request on the configured origin.
    /// </summary>
    public string? RedirectHosts { get; set; }

    /// <summary>Optional timeout for HTTP requests. Defaults to 30 seconds.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public override string ToString() =>
        $"CalDavOptions {{ BaseUrl = {BaseUrl}, AuthenticationScheme = {EffectiveAuthenticationScheme}, Username = {Username}, Password = *** }}";

    /// <summary>The configured scheme, with an omitted value resolved to Basic.</summary>
    internal string EffectiveAuthenticationScheme => string.IsNullOrEmpty(AuthenticationScheme)
        ? CalDavAuthenticationSchemes.Basic
        : AuthenticationScheme;
}

/// <summary>
/// Validates <see cref="CalDavOptions"/> at startup using <c>IValidateOptions</c> pattern.
/// </summary>
internal sealed class ValidateCalDavOptions : IValidateOptions<CalDavOptions>
{
    public ValidateOptionsResult Validate(string? name, CalDavOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            failures.Add("CalDav:BaseUrl is required.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"CalDav:BaseUrl must be a valid HTTP or HTTPS URL. Received: '{options.BaseUrl}'.");
        }
        else if (!IsSafeCanonicalEndpoint(options.BaseUrl, uri))
        {
            failures.Add("CalDav:BaseUrl must be canonical and must not contain credentials, a query, a fragment, or encoded path traversal.");
        }

        ValidateCredentials(options, failures);

        if (options.RequestTimeout <= TimeSpan.Zero)
            failures.Add("CalDav:RequestTimeout must be positive.");

        ValidateEvaluationTimeZone(options, failures);
        ValidateRedirectHosts(options, failures);
        ValidateInteroperabilityProfile(options.InteroperabilityProfile, failures);
        ValidateSchedulingMode(options.SchedulingMode, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateCredentials(CalDavOptions options, ICollection<string> failures)
    {
        switch (options.EffectiveAuthenticationScheme)
        {
            case CalDavAuthenticationSchemes.Basic:
                if (string.IsNullOrWhiteSpace(options.Username))
                    failures.Add("CalDav:Username is required.");
                if (string.IsNullOrWhiteSpace(options.Password))
                    failures.Add("CalDav:Password is required.");
                break;
            case CalDavAuthenticationSchemes.Bearer:
                ValidateBearerCredentials(options, failures);
                break;
            case CalDavAuthenticationSchemes.OAuth2:
                ValidateOAuthCredentials(options, failures);
                return;
            default:
                failures.Add(
                    $"CalDav:AuthenticationScheme must be '{CalDavAuthenticationSchemes.Basic}', " +
                    $"'{CalDavAuthenticationSchemes.Bearer}', or '{CalDavAuthenticationSchemes.OAuth2}' when specified.");
                return;
        }
        if (HasAnyOAuthSetting(options))
            failures.Add("CalDav:OAuthTokenEndpoint, OAuthClientId, OAuthClientSecret, and OAuthRefreshToken apply only when CalDav:AuthenticationScheme is 'oauth2'.");
    }

    private static bool HasAnyOAuthSetting(CalDavOptions options) =>
        !string.IsNullOrEmpty(options.OAuthTokenEndpoint)
        || !string.IsNullOrEmpty(options.OAuthClientId)
        || !string.IsNullOrEmpty(options.OAuthClientSecret)
        || !string.IsNullOrEmpty(options.OAuthRefreshToken);

    private static void ValidateOAuthCredentials(CalDavOptions options, ICollection<string> failures)
    {
        if (!string.IsNullOrEmpty(options.Username) || !string.IsNullOrEmpty(options.Password))
            failures.Add("CalDav:Username and CalDav:Password must be empty when CalDav:AuthenticationScheme is 'oauth2'; access tokens come from the refresh-token grant.");
        if (!IsSecureTokenEndpoint(options.OAuthTokenEndpoint))
            failures.Add("CalDav:OAuthTokenEndpoint is required and must be an absolute HTTPS URL without credentials or a fragment when CalDav:AuthenticationScheme is 'oauth2'.");
        if (string.IsNullOrWhiteSpace(options.OAuthClientId))
            failures.Add("CalDav:OAuthClientId is required when CalDav:AuthenticationScheme is 'oauth2'.");
        if (string.IsNullOrWhiteSpace(options.OAuthRefreshToken))
            failures.Add("CalDav:OAuthRefreshToken is required when CalDav:AuthenticationScheme is 'oauth2'.");
    }

    private static bool IsSecureTokenEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    private static void ValidateBearerCredentials(CalDavOptions options, ICollection<string> failures)
    {
        if (!string.IsNullOrEmpty(options.Username))
            failures.Add("CalDav:Username must be empty when CalDav:AuthenticationScheme is 'bearer'; the token is sent without a username.");
        if (string.IsNullOrWhiteSpace(options.Password))
            failures.Add("CalDav:Password is required and holds the token when CalDav:AuthenticationScheme is 'bearer'.");
        else if (!BearerTokenSyntax.IsValid(options.Password))
            failures.Add("CalDav:Password must be a single bearer token of visible ASCII characters without spaces when CalDav:AuthenticationScheme is 'bearer'.");
    }

    private static void ValidateEvaluationTimeZone(CalDavOptions options, ICollection<string> failures)
    {
        if (options.EvaluationTimeZone is not null && !IanaTimeZoneIds.IsValid(options.EvaluationTimeZone))
            failures.Add("CalDav:EvaluationTimeZone must be an exact IANA time-zone identifier when configured.");
    }

    private static void ValidateRedirectHosts(CalDavOptions options, ICollection<string> failures)
    {
        if (!CalDavRedirectHostRule.TryParseList(options.RedirectHosts, out var rules))
        {
            failures.Add("CalDav:RedirectHosts must be a comma-separated list of DNS host names or leading-dot domain suffixes, such as 'p01-caldav.icloud.com' or '.icloud.com', without schemes, ports, paths, wildcards, or IP addresses.");
            return;
        }
        if (rules.Count > 0
            && Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            && baseUri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add("CalDav:RedirectHosts requires an HTTPS CalDav:BaseUrl.");
        }
    }

    private static bool IsSafeCanonicalEndpoint(string original, Uri uri) =>
        string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && !original.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        && !original.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        && !original.Contains("%5c", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(original, uri.AbsoluteUri, StringComparison.Ordinal)
            || string.Equals(original + '/', uri.AbsoluteUri, StringComparison.Ordinal));

    private static bool IsSupportedInteroperabilityProfile(string? profile) =>
        string.IsNullOrEmpty(profile)
        || string.Equals(profile, CalDavInteroperabilityProfiles.Radicale_3_7_8, StringComparison.Ordinal);

    private static void ValidateInteroperabilityProfile(string? profile, ICollection<string> failures)
    {
        if (!IsSupportedInteroperabilityProfile(profile))
            failures.Add($"CalDav:InteroperabilityProfile must be '{CalDavInteroperabilityProfiles.Radicale_3_7_8}' when specified.");
    }

    private static void ValidateSchedulingMode(string? mode, ICollection<string> failures)
    {
        if (!CalDavSchedulingModes.IsSupported(mode))
            failures.Add($"CalDav:SchedulingMode must be '{CalDavSchedulingModes.StorageOnly}' or '{CalDavSchedulingModes.ServerManaged}' when specified.");
    }
}

/// <summary>Closed set of scheduling models. See ADR 0009.</summary>
public static class CalDavSchedulingModes
{
    /// <summary>Default: participation writes require proof that the server does not schedule automatically.</summary>
    public const string StorageOnly = "storage_only";

    /// <summary>Opt-in: the server may schedule automatically; affected outcomes disclose possible side effects.</summary>
    public const string ServerManaged = "server_managed";

    internal static bool IsSupported(string? mode) =>
        string.IsNullOrEmpty(mode)
        || string.Equals(mode, StorageOnly, StringComparison.Ordinal)
        || string.Equals(mode, ServerManaged, StringComparison.Ordinal);

    /// <summary>Whether validated options opted into server-managed scheduling.</summary>
    public static bool IsServerManaged(CalDavOptions options) =>
        string.Equals(options.SchedulingMode, ServerManaged, StringComparison.Ordinal);
}

/// <summary>Closed set of HTTP authentication schemes accepted by <see cref="CalDavOptions.AuthenticationScheme"/>.</summary>
public static class CalDavAuthenticationSchemes
{
    /// <summary>HTTP Basic authentication with a username and password.</summary>
    public const string Basic = "basic";

    /// <summary>A static bearer token, such as one issued for a gateway or proxy.</summary>
    public const string Bearer = "bearer";

    /// <summary>Bearer access tokens obtained and renewed through the OAuth 2.0 refresh-token grant.</summary>
    public const string OAuth2 = "oauth2";
}

internal static class BearerTokenSyntax
{
    /// <summary>A header-safe credential: one or more visible ASCII characters without whitespace.</summary>
    internal static bool IsValid(string value) => value.Length > 0
        && value.All(character => character is > ' ' and < '\u007f');
}

/// <summary>Closed set of server runtimes with verified atomic mutation preconditions.</summary>
public static class CalDavInteroperabilityProfiles
{
    public const string Radicale_3_7_8 = "radicale-3.7.8";
}

internal static class IanaTimeZoneIds
{
    internal static bool IsValid(string value) => value.Length > 0
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(value) is not null;
}
