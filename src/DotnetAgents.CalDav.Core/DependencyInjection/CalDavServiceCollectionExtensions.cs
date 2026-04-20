using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;

namespace DotnetAgents.CalDav.Core.DependencyInjection;

/// <summary>
/// Extension methods for registering CalDAV task services with <see cref="IServiceCollection"/>.
/// </summary>
public static class CalDavServiceCollectionExtensions
{
    /// <summary>
    /// Registers the CalDAV task client and related services.
    /// Configures <see cref="CalDavOptions"/> with validation-on-start semantics.
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Action to configure <see cref="CalDavOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCalDavTasks(
        this IServiceCollection services,
        Action<CalDavOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Register options with IValidateOptions + ValidateOnStart for fail-fast at startup
        services.AddOptions<CalDavOptions>()
            .Configure(configure)
            .ValidateOnStart();

        // Register the IValidateOptions implementation for complex cross-property validation
        services.AddSingleton<IValidateOptions<CalDavOptions>, ValidateCalDavOptions>();

        // Register ICalDavClient as a typed HttpClient. AddHttpClient creates a transient
        // ICalDavClient per request; IHttpClientFactory pools HttpMessageHandlers for DNS refresh
        // while creating new HttpClient instances. Register ITaskService as transient to match
        // the typed-client lifetime — a singleton would capture a stale/transient client forever.
        //
        // Disable auto-redirect: CalDAV uses non-standard HTTP methods (PROPFIND, REPORT, MKCOL)
        // that must be preserved across redirects. The default HttpClientHandler converts
        // 301/302 redirects to GET, dropping the original method and body.
        services.AddHttpClient<ICalDavClient, CalDavClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<CalDavOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = options.RequestTimeout;

            // Configure Basic authentication
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        });

        // ITaskService is transient to match the ICalDavClient typed-client lifetime.
        services.AddTransient<ITaskService, TaskService>();
        services.AddSingleton<ITaskListResolver, TaskListResolver>();

        return services;
    }
}
