using System.Net;
using System.Net.Http.Headers;
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

namespace DotnetAgents.CalDav.Core.DependencyInjection;

/// <summary>
/// Extension methods for registering CalDAV Calendar services with <see cref="IServiceCollection"/>.
/// </summary>
public static class CalDavServiceCollectionExtensions
{
    /// <summary>
    /// Registers the CalDAV Calendar client and related services.
    /// Configures <see cref="CalDavOptions"/> with validation-on-start semantics.
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Action to configure <see cref="CalDavOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCalDavCalendars(
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

        // AddHttpClient creates a transient client per request while IHttpClientFactory pools
        // HttpMessageHandlers for DNS refresh.
        //
        // SocketsHttpHandler is used instead of HttpClientHandler for:
        // - PooledConnectionLifetime: proactively recycle stale connections before the server
        //   can drop them (prevents "response ended prematurely" / ResponseEnded errors)
        // - PooledConnectionIdleTimeout: recycle idle connections before the pinned Radicale
        //   profile's 30-second server timeout can close them
        //
        // Auto-redirect is disabled because CalDAV uses non-standard HTTP methods (PROPFIND, REPORT,
        // MKCOL) that must be preserved across redirects. Disable automatic redirects explicitly
        // so a cross-origin Location cannot receive the configured Basic credentials.
        //
        // Standard resilience handler adds retry with exponential backoff (handles HttpRequestException
        // including HttpIOException/ResponseEnded from transient connection drops), circuit breaker,
        // attempt timeout, and total request timeout — all configured via Polly v8 resilience pipeline.
        services.AddTransient<CalendarHttpAttemptHandler>();
        services.AddSingleton<CalendarQueryCapabilityState>();
        var calendarClientBuilder = services.AddHttpClient<CalDavClient>((serviceProvider, client) =>
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
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20)
        });
        calendarClientBuilder.AddStandardResilienceHandler(options =>
        {
            // Three total attempts are available only to idempotent reads. A conditional write can
            // still have an ambiguous transport outcome and must be reconciled before another dispatch.
            options.Retry.MaxRetryAttempts = 2;
            options.Retry.DisableForUnsafeHttpMethods();
            options.Retry.DisableFor(new HttpMethod("MOVE"));
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromMilliseconds(200);
            var standardShouldHandle = options.Retry.ShouldHandle;
            options.Retry.ShouldHandle = arguments =>
                IsDefinitiveUnsupportedReport(arguments.Outcome.Result)
                    ? PredicateResult.False()
                    : standardShouldHandle(arguments);

            // Circuit breaker is configured with a high minimum throughput (default 100) which
            // effectively disables it for low-volume CalDAV clients. This is intentional —
            // circuit breaker is more useful for high-throughput service-to-service scenarios.
            // SamplingDuration must be >= 2x AttemptTimeout.Timeout (validation rule).
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        });
        calendarClientBuilder.AddHttpMessageHandler<CalendarHttpAttemptHandler>();

        services.AddTransient<ICalendarClient>(serviceProvider => serviceProvider.GetRequiredService<CalDavClient>());
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ICalendarEntityIdentityGenerator, CalendarEntityIdentityGenerator>();
        services.AddSingleton(serviceProvider => new CalendarQueryCursorKey(
            serviceProvider.GetRequiredService<IOptions<CalDavOptions>>()));
        services.AddSingleton(serviceProvider => new CalendarQueryCursorIssuer(
            serviceProvider.GetRequiredService<CalendarQueryCursorKey>()));
        services.AddSingleton(serviceProvider => new CalendarQueryCursorAuthenticator(
            serviceProvider.GetRequiredService<CalendarQueryCursorKey>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(serviceProvider => new CalendarQuerySnapshotStore(
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddTransient(serviceProvider => new CalendarQuerySnapshotReader(
            serviceProvider.GetRequiredService<CalendarQuerySnapshotStore>()));
        services.AddTransient(serviceProvider => new CalendarQuerySnapshotWriter(
            serviceProvider.GetRequiredService<CalendarQuerySnapshotStore>()));
        services.AddTransient(serviceProvider => new CalendarEntityQueryPageCodec(
            serviceProvider.GetRequiredService<CalendarQueryCursorIssuer>()));
        services.AddTransient(serviceProvider => new CalendarOccurrenceQueryPageCodec(
            serviceProvider.GetRequiredService<CalendarQueryCursorIssuer>()));
        services.AddTransient(serviceProvider => new CalendarDiscoveryPolicy(
            serviceProvider.GetRequiredService<IOptions<CalDavOptions>>(),
            serviceProvider.GetRequiredService<ILogger<CalendarService>>()));
        services.AddTransient(serviceProvider =>
        {
            var policy = serviceProvider.GetRequiredService<CalendarDiscoveryPolicy>();
            return new CalendarOperationDiscovery(
                serviceProvider.GetRequiredService<ICalendarClient>(),
                serviceProvider.GetRequiredService<IOptions<CalDavOptions>>(),
                policy.ApplyScope,
                policy.ResolveDefault);
        });
        services.AddTransient<ICalendarQueryTransport>(serviceProvider => new CalendarQueryTransport(
            serviceProvider.GetRequiredService<CalendarOperationDiscovery>()));
        services.AddSingleton<CalendarQueryResourceRetriever>();
        services.AddTransient(serviceProvider => new CalendarQueryAcquisitionExecutor(
            () => serviceProvider.GetRequiredService<ICalendarQueryTransport>(),
            serviceProvider.GetRequiredService<IOptions<CalDavOptions>>(),
            serviceProvider.GetRequiredService<CalendarQueryResourceRetriever>()));
        services.AddTransient(serviceProvider => new CalendarTemporalContextResolver(
            serviceProvider.GetRequiredService<IOptions<CalDavOptions>>()));
        services.AddTransient(serviceProvider => new CalendarEntityQueryStartExecutor(
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<CalendarQuerySnapshotWriter>(),
            serviceProvider.GetRequiredService<CalendarEntityQueryPageCodec>(),
            serviceProvider.GetRequiredService<CalendarQueryAcquisitionExecutor>(),
            serviceProvider.GetRequiredService<CalendarTemporalContextResolver>()));
        services.AddTransient(serviceProvider => new CalendarEntityQueryContinueExecutor(
            serviceProvider.GetRequiredService<CalendarQueryCursorAuthenticator>(),
            serviceProvider.GetRequiredService<CalendarQuerySnapshotReader>(),
            serviceProvider.GetRequiredService<CalendarEntityQueryPageCodec>()));
        services.AddTransient(serviceProvider => new CalendarOccurrenceQueryStartExecutor(
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<CalendarQuerySnapshotWriter>(),
            serviceProvider.GetRequiredService<CalendarOccurrenceQueryPageCodec>(),
            serviceProvider.GetRequiredService<CalendarQueryAcquisitionExecutor>(),
            serviceProvider.GetRequiredService<CalendarTemporalContextResolver>()));
        services.AddTransient(serviceProvider => new CalendarOccurrenceQueryContinueExecutor(
            serviceProvider.GetRequiredService<CalendarQueryCursorAuthenticator>(),
            serviceProvider.GetRequiredService<CalendarQuerySnapshotReader>(),
            serviceProvider.GetRequiredService<CalendarOccurrenceQueryPageCodec>()));
        services.AddTransient<ICalendarQueryModule>(serviceProvider => new CalendarQueryModule(
            serviceProvider.GetRequiredService<CalendarEntityQueryStartExecutor>(),
            serviceProvider.GetRequiredService<CalendarEntityQueryContinueExecutor>(),
            serviceProvider.GetRequiredService<CalendarOccurrenceQueryStartExecutor>(),
            serviceProvider.GetRequiredService<CalendarOccurrenceQueryContinueExecutor>()));
        services.AddTransient<ICalendarService, CalendarService>();

        return services;
    }

    private static bool IsDefinitiveUnsupportedReport(HttpResponseMessage? response) =>
        response?.RequestMessage?.Method.Method == "REPORT"
        && response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented;
}
