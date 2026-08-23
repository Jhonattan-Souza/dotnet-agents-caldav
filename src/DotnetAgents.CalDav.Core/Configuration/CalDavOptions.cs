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

    /// <summary>Username for Basic / Bearer authentication.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password or token for authentication.</summary>
    public string Password { get; set; } = string.Empty;

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

    /// <summary>Optional timeout for HTTP requests. Defaults to 30 seconds.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public override string ToString() =>
        $"CalDavOptions {{ BaseUrl = {BaseUrl}, Username = {Username}, Password = *** }}";
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

        if (string.IsNullOrWhiteSpace(options.Username))
            failures.Add("CalDav:Username is required.");

        if (string.IsNullOrWhiteSpace(options.Password))
            failures.Add("CalDav:Password is required.");

        if (options.RequestTimeout <= TimeSpan.Zero)
            failures.Add("CalDav:RequestTimeout must be positive.");

        ValidateEvaluationTimeZone(options, failures);
        ValidateInteroperabilityProfile(options.InteroperabilityProfile, failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateEvaluationTimeZone(CalDavOptions options, ICollection<string> failures)
    {
        if (options.EvaluationTimeZone is not null && !IanaTimeZoneIds.IsValid(options.EvaluationTimeZone))
            failures.Add("CalDav:EvaluationTimeZone must be an exact IANA time-zone identifier when configured.");
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
