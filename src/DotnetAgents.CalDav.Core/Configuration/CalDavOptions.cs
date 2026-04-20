using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Configuration;

/// <summary>
/// Configuration options for the CalDAV client.
/// Bound from configuration via <c>AddCalDavTasks</c> DI extension.
/// </summary>
public sealed class CalDavOptions
{
    public const string SectionName = "CalDav";

    /// <summary>Base URL of the CalDAV server (e.g. <c>https://caldav.example.com</c>).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Username for Basic / Bearer authentication.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password or token for authentication.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Comma-separated list of calendar hrefs to restrict task list discovery. Empty = discover all.</summary>
    public string? TaskLists { get; set; }

    /// <summary>Display name of the default task list used when no explicit list is specified by the user.</summary>
    public string? DefaultTaskList { get; set; }

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

        if (string.IsNullOrWhiteSpace(options.Username))
            failures.Add("CalDav:Username is required.");

        if (string.IsNullOrWhiteSpace(options.Password))
            failures.Add("CalDav:Password is required.");

        if (options.RequestTimeout <= TimeSpan.Zero)
            failures.Add("CalDav:RequestTimeout must be positive.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
