using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>One Authorization header value for the configured CalDAV origin.</summary>
internal sealed record CalDavCredential(string Scheme, string Parameter)
{
    internal AuthenticationHeaderValue ToHeader() => new(Scheme, Parameter);

    // Records print their members; a credential must never reach a log or exception message.
    public override string ToString() => $"CalDavCredential {{ Scheme = {Scheme}, Parameter = *** }}";
}

/// <summary>Supplies the credential the authentication handler attaches to each CalDAV request.</summary>
internal abstract class CalDavCredentialSource
{
    internal abstract ValueTask<CalDavCredential> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns a credential to replace one the server answered with 401, or null when this source
    /// cannot renew credentials and the 401 is the final outcome.
    /// </summary>
    internal virtual ValueTask<CalDavCredential?> RenewAsync(
        CalDavCredential rejected,
        CancellationToken cancellationToken) => ValueTask.FromResult<CalDavCredential?>(null);

    internal static CalDavCredentialSource Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<CalDavOptions>>().Value;
        return options.EffectiveAuthenticationScheme switch
        {
            CalDavAuthenticationSchemes.OAuth2 => new CalDavOAuthCredentialSource(
                options,
                services.GetRequiredService<IHttpClientFactory>(),
                services.GetRequiredService<TimeProvider>()),
            CalDavAuthenticationSchemes.Bearer => new StaticCalDavCredentialSource(new("Bearer", options.Password)),
            _ => new StaticCalDavCredentialSource(new(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"))))
        };
    }
}

/// <summary>A configured Basic or Bearer credential that never changes during the process lifetime.</summary>
internal sealed class StaticCalDavCredentialSource(CalDavCredential credential) : CalDavCredentialSource
{
    internal override ValueTask<CalDavCredential> GetAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(credential);
}

/// <summary>
/// Attaches the configured credential per HTTP attempt, and only to requests on the configured
/// CalDAV origin. Redirects are followed manually above this handler with same-origin validation;
/// this check keeps credentials off any other origin even if a caller bypasses that validation.
/// A renewable credential rejected with 401 is renewed and the request resent exactly once.
/// </summary>
internal sealed class CalDavAuthenticationHandler(
    CalDavCredentialSource credentials,
    IOptions<CalDavOptions> options) : DelegatingHandler
{
    private readonly Uri _origin = new(options.Value.BaseUrl, UriKind.Absolute);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = null;
        if (request.RequestUri is not { IsAbsoluteUri: true } requestUri || !HasConfiguredOrigin(requestUri))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var credential = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = credential.ToHeader();
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        CalDavCredential? renewed;
        try
        {
            renewed = await credentials.RenewAsync(credential, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        if (renewed is null)
            return response;

        // Authentication precedes request processing, so a 401 proves this request had no effect and
        // one resend is safe for every method. The resend's response is final, even another 401.
        response.Dispose();
        request.Headers.Authorization = renewed.ToHeader();
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private bool HasConfiguredOrigin(Uri requestUri) =>
        string.Equals(_origin.Scheme, requestUri.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_origin.Host, requestUri.Host, StringComparison.OrdinalIgnoreCase)
        && _origin.Port == requestUri.Port;
}
