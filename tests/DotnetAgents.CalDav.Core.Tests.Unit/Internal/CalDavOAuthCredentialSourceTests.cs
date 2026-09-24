using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

[Collection("ActivityListener")]
public sealed class CalDavOAuthCredentialSourceTests
{
    private const string TokenEndpoint = "https://oauth2.example.com/token?tenant=calendar";
    private const string ClientSecret = "client-secret-sentinel";
    private const string RefreshToken = "refresh-token-sentinel";
    private const string ResourceHref = "https://apidata.example.com/caldav/v2/user/events/a.ics";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-24T12:00:00Z");

    [Fact]
    public async Task Refresh_token_grant_posts_client_credentials_in_the_form_body()
    {
        var tokens = new TokenEndpointHandler(TokenResponse("access-1", 3600));
        var source = CreateSource(tokens);

        var credential = await source.GetAsync(TestContext.Current.CancellationToken);

        credential.ShouldBe(new CalDavCredential("Bearer", "access-1"));
        var request = tokens.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri(TokenEndpoint));
        request.Authorization.ShouldBeNull();
        request.Accept.ShouldBe("application/json");
        request.ContentType.ShouldBe("application/x-www-form-urlencoded");
        request.Form.ShouldBe(
            $"grant_type=refresh_token&refresh_token={RefreshToken}&client_id=client-id&client_secret={ClientSecret}");
    }

    [Fact]
    public async Task Public_client_omits_the_client_secret()
    {
        var tokens = new TokenEndpointHandler(TokenResponse("access-1", 3600));
        var source = CreateSource(tokens, configure: options => options.OAuthClientSecret = string.Empty);

        await source.GetAsync(TestContext.Current.CancellationToken);

        tokens.Requests.ShouldHaveSingleItem().Form.ShouldBe(
            $"grant_type=refresh_token&refresh_token={RefreshToken}&client_id=client-id");
    }

    [Theory]
    [InlineData(3600, 3540)]
    [InlineData(60, 30)]
    [InlineData(1, 0.5)]
    public async Task Access_token_is_cached_until_expiry_minus_skew(long expiresIn, double refreshAfterSeconds)
    {
        var tokens = new TokenEndpointHandler(
            TokenResponse("access-1", expiresIn),
            TokenResponse("access-2", expiresIn));
        var time = new ManualTimeProvider(Start);
        var source = CreateSource(tokens, time);
        var cancellationToken = TestContext.Current.CancellationToken;

        (await source.GetAsync(cancellationToken)).Parameter.ShouldBe("access-1");
        time.Advance(TimeSpan.FromSeconds(refreshAfterSeconds) - TimeSpan.FromTicks(1));
        (await source.GetAsync(cancellationToken)).Parameter.ShouldBe("access-1");
        tokens.Requests.Count.ShouldBe(1);
        time.Advance(TimeSpan.FromTicks(1));

        (await source.GetAsync(cancellationToken)).Parameter.ShouldBe("access-2");
        tokens.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Access_token_without_expires_in_is_kept_until_rejected()
    {
        var tokens = new TokenEndpointHandler(TokenResponse("access-1", null), TokenResponse("access-2", null));
        var time = new ManualTimeProvider(Start);
        var source = CreateSource(tokens, time);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await source.GetAsync(cancellationToken);
        time.Advance(TimeSpan.FromDays(30));
        (await source.GetAsync(cancellationToken)).ShouldBeSameAs(first);
        var renewed = await source.RenewAsync(first, cancellationToken);

        renewed.ShouldNotBeNull().Parameter.ShouldBe("access-2");
        tokens.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_in_flight_token_request()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new TokenEndpointHandler(async () =>
        {
            await release.Task;
            return TokenResponse("access-1", 3600)();
        });
        var source = CreateSource(tokens);
        var cancellationToken = TestContext.Current.CancellationToken;

        var callers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () => await source.GetAsync(cancellationToken), cancellationToken))
            .ToArray();
        await tokens.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        release.SetResult();
        var credentials = await Task.WhenAll(callers);

        tokens.Requests.Count.ShouldBe(1);
        credentials.ShouldAllBe(credential => credential.Parameter == "access-1");
    }

    [Fact]
    public async Task Concurrent_renewals_of_one_rejected_token_refresh_once()
    {
        var tokens = new TokenEndpointHandler(TokenResponse("access-1", 3600), TokenResponse("access-2", 3600));
        var source = CreateSource(tokens);
        var cancellationToken = TestContext.Current.CancellationToken;
        var rejected = await source.GetAsync(cancellationToken);

        var renewals = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await source.RenewAsync(rejected, cancellationToken), cancellationToken)));

        tokens.Requests.Count.ShouldBe(2);
        renewals.ShouldAllBe(credential => credential!.Parameter == "access-2");
    }

    [Fact]
    public async Task Rotated_refresh_token_replaces_the_configured_one_in_memory()
    {
        var tokens = new TokenEndpointHandler(
            TokenResponse("access-1", 3600, refreshToken: "rotated-refresh"),
            TokenResponse("access-2", 3600));
        var source = CreateSource(tokens);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await source.GetAsync(cancellationToken);
        await source.RenewAsync(first, cancellationToken);

        tokens.Requests[0].Form.ShouldContain($"refresh_token={RefreshToken}&");
        tokens.Requests[1].Form.ShouldContain("refresh_token=rotated-refresh&");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Rejected_grant_is_a_typed_401_failure_without_the_response_body(HttpStatusCode status)
    {
        var tokens = new TokenEndpointHandler(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(
                "{\"error\":\"invalid_grant\",\"error_description\":\"body-sentinel for " + RefreshToken + "\"}",
                Encoding.UTF8,
                "application/json")
        });
        var source = CreateSource(tokens);

        var exception = await Should.ThrowAsync<CalDavAuthenticationException>(
            () => source.GetAsync(TestContext.Current.CancellationToken).AsTask());

        exception.Failure.ShouldBe(CalDavAuthenticationFailure.Rejected);
        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        exception.Message.ShouldBe("The OAuth token endpoint rejected the refresh-token grant.");
        AssertCarriesNoSecret(exception);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Other_token_endpoint_statuses_are_unavailable(HttpStatusCode status)
    {
        var tokens = new TokenEndpointHandler(() => new HttpResponseMessage(status)
        {
            Headers = { Location = new Uri("https://elsewhere.example/token") },
            Content = new StringContent("body-sentinel")
        });
        var source = CreateSource(tokens);

        var exception = await Should.ThrowAsync<CalDavAuthenticationException>(
            () => source.GetAsync(TestContext.Current.CancellationToken).AsTask());

        exception.Failure.ShouldBe(CalDavAuthenticationFailure.Unavailable);
        exception.StatusCode.ShouldBeNull();
        tokens.Requests.ShouldHaveSingleItem();
        AssertCarriesNoSecret(exception);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"token_type\":\"Bearer\"}")]
    [InlineData("{\"access_token\":42,\"token_type\":\"Bearer\"}")]
    [InlineData("{\"access_token\":\"has space\",\"token_type\":\"Bearer\"}")]
    [InlineData("{\"access_token\":\"\",\"token_type\":\"Bearer\"}")]
    [InlineData("{\"access_token\":\"body-sentinel\"}")]
    [InlineData("{\"access_token\":\"body-sentinel\",\"token_type\":\"mac\"}")]
    [InlineData("{\"access_token\":\"body-sentinel\",\"token_type\":\"Bearer\",\"expires_in\":\"3600\"}")]
    [InlineData("{\"access_token\":\"body-sentinel\",\"token_type\":\"Bearer\",\"expires_in\":0}")]
    [InlineData("{\"access_token\":\"body-sentinel\",\"token_type\":\"Bearer\",\"expires_in\":1.5}")]
    [InlineData("{\"access_token\":\"body-sentinel\",\"token_type\":\"Bearer\",\"expires_in\":4294967296}")]
    [InlineData("{\"access_token\":\"body-sentinel\",\"token_type\":\"Bearer\",\"refresh_token\":\"\"}")]
    [InlineData("{\"access_token\":\"body-sentinel\",\"token_type\":\"Bearer\",\"refresh_token\":7}")]
    public async Task Unusable_token_response_is_an_invalid_response_failure(string body)
    {
        var tokens = new TokenEndpointHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        var source = CreateSource(tokens);

        var exception = await Should.ThrowAsync<CalDavAuthenticationException>(
            () => source.GetAsync(TestContext.Current.CancellationToken).AsTask());

        exception.Failure.ShouldBe(CalDavAuthenticationFailure.InvalidResponse);
        exception.StatusCode.ShouldBeNull();
        AssertCarriesNoSecret(exception);
    }

    [Fact]
    public async Task Lowercase_bearer_token_type_is_accepted()
    {
        var tokens = new TokenEndpointHandler(() => Json("{\"access_token\":\"access-1\",\"token_type\":\"bearer\"}"));
        var source = CreateSource(tokens);

        (await source.GetAsync(TestContext.Current.CancellationToken)).Parameter.ShouldBe("access-1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_token_response_is_rejected_without_buffering_it(bool declaredLength)
    {
        var padding = new string('x', CalDavOAuthCredentialSource.MaximumResponseBytes);
        var tokens = new TokenEndpointHandler(() =>
        {
            var bytes = Encoding.UTF8.GetBytes("{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"pad\":\"" + padding + "\"}");
            HttpContent content = declaredLength
                ? new ByteArrayContent(bytes)
                : new StreamContent(new UnknownLengthStream(bytes));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var source = CreateSource(tokens);

        var exception = await Should.ThrowAsync<CalDavAuthenticationException>(
            () => source.GetAsync(TestContext.Current.CancellationToken).AsTask());

        exception.Failure.ShouldBe(CalDavAuthenticationFailure.InvalidResponse);
    }

    [Fact]
    public async Task Token_request_is_bounded_by_its_own_timeout()
    {
        var tokens = new TokenEndpointHandler(
            () => new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously).Task,
            honorCancellation: true);
        var time = new ManualTimeProvider(Start);
        var source = CreateSource(tokens, time);

        var pending = source.GetAsync(TestContext.Current.CancellationToken).AsTask();
        await tokens.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        time.Advance(CalDavOAuthCredentialSource.RequestTimeout);
        var exception = await Should.ThrowAsync<CalDavAuthenticationException>(() => pending);

        exception.Failure.ShouldBe(CalDavAuthenticationFailure.Unavailable);
        exception.Message.ShouldBe("The OAuth token endpoint did not respond within its time limit.");
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_token_failure()
    {
        var tokens = new TokenEndpointHandler(
            () => new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously).Task,
            honorCancellation: true);
        var source = CreateSource(tokens);
        using var cancellation = new CancellationTokenSource();

        var pending = source.GetAsync(cancellation.Token).AsTask();
        await tokens.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        var exception = await Should.ThrowAsync<OperationCanceledException>(() => pending);
        exception.ShouldNotBeOfType<CalDavAuthenticationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_failures_are_unavailable_without_their_inner_messages(bool failWhileReadingBody)
    {
        var tokens = new TokenEndpointHandler(() => failWhileReadingBody
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) }
            : throw new HttpRequestException("connect to oauth2.example.com failed body-sentinel"));
        var source = CreateSource(tokens);

        var exception = await Should.ThrowAsync<CalDavAuthenticationException>(
            () => source.GetAsync(TestContext.Current.CancellationToken).AsTask());

        exception.Failure.ShouldBe(CalDavAuthenticationFailure.Unavailable);
        exception.Message.ShouldBe("The OAuth token endpoint could not be reached.");
        exception.InnerException.ShouldBeNull();
        AssertCarriesNoSecret(exception);
    }

    [Fact]
    public async Task CalDav_requests_carry_the_access_token_and_share_the_cached_token()
    {
        var tokens = new TokenEndpointHandler(TokenResponse("access-1", 3600));
        var calDav = new CalDavHandler(_ => HttpStatusCode.OK);
        using var provider = BuildProvider(tokens, calDav);
        var client = provider.GetRequiredService<CalDavClient>();
        var cancellationToken = TestContext.Current.CancellationToken;

        await client.GetCalendarResourceAsync(ResourceHref, cancellationToken);
        await client.GetCalendarResourceAsync(ResourceHref, cancellationToken);

        tokens.Requests.Count.ShouldBe(1);
        calDav.Requests.Select(request => request.Authorization).ShouldBe(["Bearer access-1", "Bearer access-1"]);
    }

    [Fact]
    public async Task CalDav_401_renews_once_and_resends_the_same_request()
    {
        var tokens = new TokenEndpointHandler(TokenResponse("access-1", 3600), TokenResponse("access-2", 3600));
        var calDav = new CalDavHandler(request => request.Authorization == "Bearer access-1"
            ? HttpStatusCode.Unauthorized
            : HttpStatusCode.MultiStatus);
        using var provider = BuildProvider(tokens, calDav);

        var response = await provider.GetRequiredService<CalDavClient>().SendProtocolRequestAsync(
            "https://apidata.example.com/caldav/v2/user/events/", "PROPPATCH", "<update/>", null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(207);
        tokens.Requests.Count.ShouldBe(2);
        calDav.Requests.Select(request => (request.Method, request.Authorization, request.Body)).ShouldBe([
            ("PROPPATCH", "Bearer access-1", "<update/>"),
            ("PROPPATCH", "Bearer access-2", "<update/>")
        ]);
    }

    [Fact]
    public async Task Repeated_CalDav_401_is_returned_after_a_single_renewal()
    {
        var tokens = new TokenEndpointHandler(TokenResponse("access-1", 3600), TokenResponse("access-2", 3600));
        var calDav = new CalDavHandler(_ => HttpStatusCode.Unauthorized);
        using var provider = BuildProvider(tokens, calDav);

        var response = await provider.GetRequiredService<CalDavClient>().SendProtocolRequestAsync(
            "https://apidata.example.com/caldav/v2/user/events/", "PROPFIND", "<propfind/>", 0,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(401);
        tokens.Requests.Count.ShouldBe(2);
        calDav.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Static_bearer_401_is_final_without_a_resend()
    {
        var calDav = new CalDavHandler(_ => HttpStatusCode.Unauthorized);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://apidata.example.com/caldav/v2/";
            options.AuthenticationScheme = CalDavAuthenticationSchemes.Bearer;
            options.Password = "static-token";
        });
        services.AddHttpClient<CalDavClient>().ConfigurePrimaryHttpMessageHandler(() => calDav);
        using var provider = services.BuildServiceProvider();

        var response = await provider.GetRequiredService<CalDavClient>().SendProtocolRequestAsync(
            "https://apidata.example.com/caldav/v2/user/events/", "PROPFIND", "<propfind/>", 0,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(401);
        calDav.Requests.ShouldHaveSingleItem().Authorization.ShouldBe("Bearer static-token");
    }

    [Fact]
    public async Task Renewal_failure_after_CalDav_401_surfaces_the_typed_failure()
    {
        var tokens = new TokenEndpointHandler(
            TokenResponse("access-1", 3600),
            () => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var calDav = new CalDavHandler(_ => HttpStatusCode.Unauthorized);
        using var provider = BuildProvider(tokens, calDav);

        var exception = await Should.ThrowAsync<CalDavAuthenticationException>(() => provider
            .GetRequiredService<CalDavClient>().SendProtocolRequestAsync(
                "https://apidata.example.com/caldav/v2/user/events/", "PROPFIND", "<propfind/>", 0,
                TestContext.Current.CancellationToken));

        exception.Failure.ShouldBe(CalDavAuthenticationFailure.Rejected);
        calDav.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Rejected_grant_is_not_retried_by_the_resilience_pipeline()
    {
        var tokens = new TokenEndpointHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var calDav = new CalDavHandler(_ => HttpStatusCode.OK);
        using var provider = BuildProvider(tokens, calDav);

        var exception = await Should.ThrowAsync<HttpRequestException>(() => provider
            .GetRequiredService<CalDavClient>().GetCalendarResourceAsync(ResourceHref, TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        tokens.Requests.Count.ShouldBe(1);
        calDav.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unavailable_token_endpoint_follows_the_read_retry_budget()
    {
        var tokens = new TokenEndpointHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var calDav = new CalDavHandler(_ => HttpStatusCode.OK);
        using var provider = BuildProvider(tokens, calDav);

        var exception = await Should.ThrowAsync<HttpRequestException>(() => provider
            .GetRequiredService<CalDavClient>().GetCalendarResourceAsync(ResourceHref, TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBeNull();
        tokens.Requests.Count.ShouldBe(3);
        calDav.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Token_endpoint_client_follows_no_redirects_and_keeps_no_cookies()
    {
        SocketsHttpHandler? handler = null;
        var services = new ServiceCollection();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new CapturingHandlerFilter((name, candidate) =>
        {
            if (name == CalDavOAuthCredentialSource.HttpClientName)
                handler = candidate as SocketsHttpHandler;
        }));
        services.AddCalDavCalendars(ConfigureOAuth);
        using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(CalDavOAuthCredentialSource.HttpClientName);

        handler.ShouldNotBeNull();
        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseCookies.ShouldBeFalse();
        var source = provider.GetRequiredService<CalDavCredentialSource>().ShouldBeOfType<CalDavOAuthCredentialSource>();
        (await Should.ThrowAsync<CalDavAuthenticationException>(
            () => source.GetAsync(TestContext.Current.CancellationToken).AsTask()))
            .Failure.ShouldBe(CalDavAuthenticationFailure.Unavailable);
    }

    [Fact]
    public async Task Http_attempt_telemetry_carries_no_token_or_secret()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarHttpTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (activities)
                    activities.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        var tokens = new TokenEndpointHandler(
            TokenResponse("access-token-sentinel", 3600),
            TokenResponse("access-token-sentinel-2", 3600),
            () => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("body-sentinel") });
        var calDav = new CalDavHandler(request => request.Authorization == "Bearer access-token-sentinel"
            ? HttpStatusCode.Unauthorized
            : HttpStatusCode.OK);
        using var provider = BuildProvider(tokens, calDav);
        var client = provider.GetRequiredService<CalDavClient>();

        var source = provider.GetRequiredService<CalDavCredentialSource>();

        await client.GetCalendarResourceAsync(ResourceHref, TestContext.Current.CancellationToken);
        var current = await source.GetAsync(TestContext.Current.CancellationToken);
        current.Parameter.ShouldBe("access-token-sentinel-2");
        await Should.ThrowAsync<CalDavAuthenticationException>(
            () => source.RenewAsync(current, TestContext.Current.CancellationToken).AsTask());
        using (var rejecting = BuildProvider(
            new TokenEndpointHandler(() => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid_client\",\"error_description\":\"body-sentinel\"}")
            }),
            calDav))
        {
            await Should.ThrowAsync<HttpRequestException>(() => rejecting.GetRequiredService<CalDavClient>()
                .GetCalendarResourceAsync(ResourceHref, TestContext.Current.CancellationToken));
        }

        activities.ShouldNotBeEmpty();
        foreach (var activity in activities)
        {
            var text = string.Join('\n', activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")
                .Concat(activity.Events.Select(item => item.Name))
                .Append(activity.DisplayName)
                .Append(activity.StatusDescription ?? string.Empty)
                .Append(activity.TraceStateString ?? string.Empty));
            text.ShouldNotContain("sentinel");
            text.ShouldNotContain("oauth2.example.com");
        }
    }

    private static void AssertCarriesNoSecret(Exception exception)
    {
        var text = exception.ToString();
        text.ShouldNotContain(ClientSecret);
        text.ShouldNotContain(RefreshToken);
        text.ShouldNotContain("sentinel");
        text.ShouldNotContain("oauth2.example.com");
    }

    private static CalDavOAuthCredentialSource CreateSource(
        TokenEndpointHandler tokens,
        TimeProvider? time = null,
        Action<CalDavOptions>? configure = null)
    {
        var options = new CalDavOptions();
        ConfigureOAuth(options);
        configure?.Invoke(options);
        return new CalDavOAuthCredentialSource(options, new SingleHandlerFactory(tokens), time ?? TimeProvider.System);
    }

    private static void ConfigureOAuth(CalDavOptions options)
    {
        options.BaseUrl = "https://apidata.example.com/caldav/v2/";
        options.AuthenticationScheme = CalDavAuthenticationSchemes.OAuth2;
        options.OAuthTokenEndpoint = TokenEndpoint;
        options.OAuthClientId = "client-id";
        options.OAuthClientSecret = ClientSecret;
        options.OAuthRefreshToken = RefreshToken;
    }

    private static ServiceProvider BuildProvider(TokenEndpointHandler tokens, CalDavHandler calDav)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(ConfigureOAuth);
        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.Delay = TimeSpan.Zero;
            options.Retry.UseJitter = false;
        });
        services.AddHttpClient<CalDavClient>().ConfigurePrimaryHttpMessageHandler(() => calDav);
        services.AddHttpClient(CalDavOAuthCredentialSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => tokens);
        return services.BuildServiceProvider();
    }

    private static Func<HttpResponseMessage> TokenResponse(string accessToken, long? expiresIn, string? refreshToken = null)
    {
        var expiry = expiresIn is { } seconds ? $",\"expires_in\":{seconds}" : string.Empty;
        var rotation = refreshToken is null ? string.Empty : $",\"refresh_token\":\"{refreshToken}\"";
        return () => Json($"{{\"access_token\":\"{accessToken}\",\"token_type\":\"Bearer\"{expiry}{rotation}}}");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed record RecordedTokenRequest(
        HttpMethod Method,
        Uri? Uri,
        AuthenticationHeaderValue? Authorization,
        string Accept,
        string? ContentType,
        string Form);

    private sealed class TokenEndpointHandler : HttpMessageHandler
    {
        private readonly Func<Task<HttpResponseMessage>>[] _responses;
        private readonly bool _honorCancellation;

        internal TokenEndpointHandler(params Func<HttpResponseMessage>[] responses)
        {
            _responses = responses.Select(response => (Func<Task<HttpResponseMessage>>)(() => Task.FromResult(response()))).ToArray();
        }

        internal TokenEndpointHandler(Func<Task<HttpResponseMessage>> response, bool honorCancellation = false)
        {
            _responses = [response];
            _honorCancellation = honorCancellation;
        }

        internal List<RecordedTokenRequest> Requests { get; } = [];

        internal TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var recorded = new RecordedTokenRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                request.Headers.Accept.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            int index;
            lock (Requests)
            {
                index = Requests.Count;
                Requests.Add(recorded);
            }
            FirstRequest.TrySetResult();
            var pending = _responses[Math.Min(index, _responses.Length - 1)]();
            return _honorCancellation ? await pending.WaitAsync(cancellationToken) : await pending;
        }
    }

    private sealed record RecordedCalDavRequest(string Method, string? Authorization, string Body);

    private sealed class CalDavHandler(Func<RecordedCalDavRequest, HttpStatusCode> status) : HttpMessageHandler
    {
        internal List<RecordedCalDavRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var recorded = new RecordedCalDavRequest(
                request.Method.Method,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            lock (Requests)
                Requests.Add(recorded);
            return new HttpResponseMessage(status(recorded))
            {
                RequestMessage = request,
                Headers = { ETag = new EntityTagHeaderValue("\"r1\"") },
                Content = new ByteArrayContent("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"u8.ToArray())
            };
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHandlerFilter(Action<string, HttpMessageHandler> capture) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            capture(builder.Name ?? string.Empty, builder.PrimaryHandler);
            builder.PrimaryHandler = new UnreachableHandler();
        };
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("unreachable oauth2.example.com");
    }

    private sealed class UnknownLengthStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("reset by body-sentinel");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow()
        {
            lock (_timers)
                return utcNow;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime);
            lock (_timers)
                _timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan amount)
        {
            ManualTimer[] timers;
            lock (_timers)
            {
                utcNow += amount;
                timers = [.. _timers];
            }
            foreach (var timer in timers)
                timer.FireIfDue();
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
                return true;
            }

            public void Dispose() => _dueAt = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void FireIfDue()
            {
                if (_dueAt is not { } dueAt || owner.GetUtcNow() < dueAt)
                    return;
                _dueAt = null;
                callback(state);
            }
        }
    }
}
