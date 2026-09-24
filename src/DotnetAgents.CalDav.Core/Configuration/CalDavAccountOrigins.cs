namespace DotnetAgents.CalDav.Core.Configuration;

/// <summary>
/// The origins that may receive CalDAV requests and the configured credentials: the configured
/// endpoint's origin plus HTTPS hosts explicitly allowlisted by <see cref="CalDavOptions.RedirectHosts"/>
/// at the configured port. Every other origin is refused before any request is sent.
/// </summary>
internal sealed class CalDavAccountOrigins
{
    private readonly Uri _configuredBaseUri;
    private readonly IReadOnlyList<CalDavRedirectHostRule> _redirectHosts;

    internal CalDavAccountOrigins(Uri configuredBaseUri, IReadOnlyList<CalDavRedirectHostRule>? redirectHosts = null)
    {
        _configuredBaseUri = configuredBaseUri;
        _redirectHosts = redirectHosts ?? [];
    }

    /// <summary>Builds the account origins, failing closed to the configured origin for an invalid allowlist.</summary>
    internal static CalDavAccountOrigins From(CalDavOptions options) => new(
        new Uri(options.BaseUrl, UriKind.Absolute),
        CalDavRedirectHostRule.TryParseList(options.RedirectHosts, out var rules) ? rules : []);

    internal bool Contains(Uri candidate) => IsConfiguredOrigin(candidate) || IsRedirectHostOrigin(candidate);

    private bool IsConfiguredOrigin(Uri candidate) =>
        string.Equals(candidate.Scheme, _configuredBaseUri.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.Host, _configuredBaseUri.Host, StringComparison.OrdinalIgnoreCase)
        && candidate.Port == _configuredBaseUri.Port;

    // An allowlisted host never changes scheme or port: both the configured endpoint and the
    // candidate must use HTTPS, and the candidate must use the configured port.
    private bool IsRedirectHostOrigin(Uri candidate) =>
        string.Equals(_configuredBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && candidate.Port == _configuredBaseUri.Port
        && _redirectHosts.Any(rule => rule.Matches(candidate.IdnHost));
}

/// <summary>
/// One <c>CALDAV_REDIRECT_HOSTS</c> entry: an exact DNS host name, or a leading-dot domain
/// suffix that matches strict subdomains only (<c>.icloud.com</c> matches <c>p01-caldav.icloud.com</c>
/// but not <c>icloud.com</c>).
/// </summary>
internal readonly record struct CalDavRedirectHostRule(string Host, bool IncludesSubdomains)
{
    private const int MaximumHostLength = 253;
    private const int MaximumLabelLength = 63;

    internal bool Matches(string host) => IncludesSubdomains
        ? host.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase)
        : string.Equals(host, Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a comma-separated allowlist. A missing or empty value means no allowlisted host.
    /// Entries are trimmed; empty entries, schemes, ports, paths, wildcards, IP literals, trailing dots,
    /// non-ASCII names, and single-label names are invalid.
    /// </summary>
    internal static bool TryParseList(string? value, out IReadOnlyList<CalDavRedirectHostRule> rules)
    {
        rules = [];
        if (string.IsNullOrEmpty(value))
            return true;
        var parsed = new List<CalDavRedirectHostRule>();
        foreach (var entry in value.Split(','))
        {
            if (!TryParse(entry.Trim(), out var rule))
                return false;
            parsed.Add(rule);
        }
        rules = parsed;
        return true;
    }

    private static bool TryParse(string entry, out CalDavRedirectHostRule rule)
    {
        var includesSubdomains = entry.StartsWith('.');
        var host = includesSubdomains ? entry[1..] : entry;
        rule = new CalDavRedirectHostRule(host.ToLowerInvariant(), includesSubdomains);
        return IsDnsHostName(host);
    }

    private static bool IsDnsHostName(string host)
    {
        var labels = host.Split('.');
        return host.Length <= MaximumHostLength
            && labels.Length >= 2
            && labels.All(IsDnsLabel)
            && !labels[^1].All(char.IsAsciiDigit);
    }

    private static bool IsDnsLabel(string label) =>
        label.Length is >= 1 and <= MaximumLabelLength
        && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
        && label[0] != '-'
        && label[^1] != '-';
}
