using System.Net;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalDavOAuthToolFailureTests
{
    private const string ClientSecret = "client-secret-sentinel";
    private const string RefreshToken = "refresh-token-sentinel";

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "upstream_unauthorized", false, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "upstream_unavailable", true, 3)]
    public async Task Token_endpoint_failure_becomes_a_typed_tool_error_without_secrets(
        HttpStatusCode tokenStatus,
        string expectedCode,
        bool expectedRetryable,
        int expectedTokenRequests)
    {
        var tokenEndpoint = new CountingHandler(() => new HttpResponseMessage(tokenStatus)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"body-sentinel\"}")
        });
        var calDav = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://apidata.example.com/caldav/v2/";
            options.AuthenticationScheme = CalDavAuthenticationSchemes.OAuth2;
            options.OAuthTokenEndpoint = "https://oauth2.example.com/token";
            options.OAuthClientId = "client-id";
            options.OAuthClientSecret = ClientSecret;
            options.OAuthRefreshToken = RefreshToken;
        });
        services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.Retry.Delay = TimeSpan.Zero;
            options.Retry.UseJitter = false;
        });
        services.AddHttpClient("DotnetAgents.CalDav.OAuth").ConfigurePrimaryHttpMessageHandler(() => tokenEndpoint);
        services.AddHttpClient<DotnetAgents.CalDav.Core.Internal.CalDavClient>()
            .ConfigurePrimaryHttpMessageHandler(() => calDav);
        await using var provider = services.BuildServiceProvider();
        var tools = new CalendarTools(provider.GetRequiredService<ICalendarService>());

        var result = await tools.ListAsync(TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(true);
        var error = result.StructuredContent!.Value.Deserialize<CalendarErrorResult>()!;
        error.Code.ShouldBe(expectedCode);
        error.Retryable.ShouldBe(expectedRetryable);
        tokenEndpoint.Count.ShouldBe(expectedTokenRequests);
        calDav.Count.ShouldBe(0);
        var serialized = JsonSerializer.Serialize(result);
        serialized.ShouldNotContain("sentinel");
        serialized.ShouldNotContain("oauth2.example.com");
    }

    private sealed class CountingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(respond());
        }
    }
}
