using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
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
        // SocketsHttpHandler is used instead of HttpClientHandler for:
        // - PooledConnectionLifetime: proactively recycle stale connections before the server
        //   can drop them (prevents "response ended prematurely" / ResponseEnded errors)
        // - PooledConnectionIdleTimeout: close idle connections that Radicale may have already closed
        //
        // Auto-redirect is disabled because CalDAV uses non-standard HTTP methods (PROPFIND, REPORT,
        // MKCOL) that must be preserved across redirects. SocketsHttpHandler does not follow
        // redirects by default, so no AllowAutoRedirect setting is needed.
        //
        // Standard resilience handler adds retry with exponential backoff (handles HttpRequestException
        // including HttpIOException/ResponseEnded from transient connection drops), circuit breaker,
        // attempt timeout, and total request timeout — all configured via Polly v8 resilience pipeline.
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
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
        })
        .AddStandardResilienceHandler(options =>
        {
            // CalDAV operations use conditional headers (If-Match, If-None-Match) that make
            // retries safe for all methods: duplicate creates get 412, duplicate updates get 412,
            // and deletes are idempotent (404 on repeat). Keep default retry behavior for all methods.
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromMilliseconds(200);

            // Circuit breaker is configured with a high minimum throughput (default 100) which
            // effectively disables it for low-volume CalDAV clients. This is intentional —
            // circuit breaker is more useful for high-throughput service-to-service scenarios.
            // SamplingDuration must be >= 2x AttemptTimeout.Timeout (validation rule).
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        });

        // ITaskService is transient to match the ICalDavClient typed-client lifetime.
        services.AddTransient<ITaskService, TaskService>();
        services.AddSingleton<ITaskListResolver, TaskListResolver>();

        return services;
    }
}
