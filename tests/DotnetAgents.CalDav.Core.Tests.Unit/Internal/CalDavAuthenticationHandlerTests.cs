using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalDavAuthenticationHandlerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(CalDavAuthenticationSchemes.Basic)]
    public async Task Basic_scheme_sends_utf8_basic_credentials(string? scheme)
    {
        var primary = new RecordingHandler(HttpStatusCode.OK);
        using var provider = BuildProvider(primary, options =>
        {
            options.AuthenticationScheme = scheme;
            options.Username = "usér";
            options.Password = "pass:word";
        });

        await provider.GetRequiredService<ICalendarClient>().GetCalendarResourceAsync(
            "https://cal.example/events/a.ics", TestContext.Current.CancellationToken);

        var header = primary.Authorizations.ShouldHaveSingleItem().ShouldNotBeNull();
        header.Scheme.ShouldBe("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!)).ShouldBe("usér:pass:word");
    }

    [Fact]
    public async Task Bearer_scheme_sends_the_configured_token_unchanged()
    {
        var primary = new RecordingHandler(HttpStatusCode.OK);
        using var provider = BuildProvider(primary, options =>
        {
            options.AuthenticationScheme = CalDavAuthenticationSchemes.Bearer;
            options.Password = "gateway-token.v1";
        });

        await provider.GetRequiredService<ICalendarClient>().GetCalendarResourceAsync(
            "https://cal.example/events/a.ics", TestContext.Current.CancellationToken);

        var header = primary.Authorizations.ShouldHaveSingleItem().ShouldNotBeNull();
        header.Scheme.ShouldBe("Bearer");
        header.Parameter.ShouldBe("gateway-token.v1");
    }

    [Fact]
    public async Task Every_resilience_attempt_carries_the_credential()
    {
        var primary = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        using var provider = BuildProvider(primary, options =>
        {
            options.AuthenticationScheme = CalDavAuthenticationSchemes.Bearer;
            options.Password = "gateway-token";
        });

        await Should.ThrowAsync<HttpRequestException>(() => provider.GetRequiredService<ICalendarClient>()
            .GetCalendarResourceAsync("https://cal.example/events/a.ics", TestContext.Current.CancellationToken));

        primary.Authorizations.Count.ShouldBe(3);
        primary.Authorizations.ShouldAllBe(header => header!.Scheme == "Bearer" && header.Parameter == "gateway-token");
    }

    [Theory]
    [InlineData("https://other.example/events/a.ics")]
    [InlineData("http://cal.example/events/a.ics")]
    [InlineData("https://cal.example:8443/events/a.ics")]
    [InlineData("events/a.ics")]
    public async Task Requests_off_the_configured_origin_never_receive_credentials(string requestUri)
    {
        var primary = new RecordingHandler(HttpStatusCode.OK);
        using var invoker = new HttpMessageInvoker(new CalDavAuthenticationHandler(
            new StaticCalDavCredentialSource(new CalDavCredential("Bearer", "secret-token")),
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example/dav/" }))
        {
            InnerHandler = primary
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(requestUri, UriKind.RelativeOrAbsolute));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-supplied");

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        primary.Authorizations.ShouldHaveSingleItem().ShouldBeNull();
    }

    [Fact]
    public async Task Cancellation_during_renewal_disposes_the_401_and_propagates()
    {
        var primary = new DisposalTrackingHandler();
        using var invoker = new HttpMessageInvoker(new CalDavAuthenticationHandler(
            new CancelledRenewalSource(),
            Options.Create(new CalDavOptions { BaseUrl = "https://cal.example/dav/" }))
        {
            InnerHandler = primary
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://cal.example/dav/a.ics");

        await Should.ThrowAsync<OperationCanceledException>(
            () => invoker.SendAsync(request, TestContext.Current.CancellationToken));

        primary.Content.ShouldNotBeNull().Disposed.ShouldBeTrue();
    }

    [Fact]
    public void Credential_text_never_contains_the_secret()
    {
        var credential = new CalDavCredential("Bearer", "secret-token");

        credential.ToString().ShouldBe("CalDavCredential { Scheme = Bearer, Parameter = *** }");
    }

    private static ServiceProvider BuildProvider(HttpMessageHandler primary, Action<CalDavOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            configure(options);
        });
        services.ConfigureAll<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>(options =>
        {
            options.Retry.Delay = TimeSpan.Zero;
            options.Retry.UseJitter = false;
        });
        services.AddHttpClient<CalDavClient>().ConfigurePrimaryHttpMessageHandler(() => primary);
        return services.BuildServiceProvider();
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        internal List<AuthenticationHeaderValue?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (Authorizations)
                Authorizations.Add(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Headers = { ETag = new EntityTagHeaderValue("\"r1\"") },
                Content = new ByteArrayContent("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"u8.ToArray())
            });
        }
    }

    private sealed class CancelledRenewalSource : CalDavCredentialSource
    {
        internal override ValueTask<CalDavCredential> GetAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CalDavCredential("Bearer", "expired-token"));

        internal override ValueTask<CalDavCredential?> RenewAsync(
            CalDavCredential rejected,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<CalDavCredential?>(new OperationCanceledException());
    }

    private sealed class DisposalTrackingHandler : HttpMessageHandler
    {
        internal TrackingContent? Content { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Content = new TrackingContent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = Content });
        }
    }

    private sealed class TrackingContent : ByteArrayContent
    {
        internal TrackingContent() : base([])
        {
        }

        internal bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
