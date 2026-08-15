using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.DependencyInjection;

public sealed class CalDavServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCalDavTasks_DisablesAutomaticRedirectsOnTheConfiguredHandler()
    {
        SocketsHttpHandler? handler = null;
        var services = new ServiceCollection();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new CapturingHandlerFilter(candidate => handler = candidate as SocketsHttpHandler));
        services.AddCalDavTasks(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
        });
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<ICalDavClient>();

        handler.ShouldNotBeNull();
        handler.AllowAutoRedirect.ShouldBeFalse();
    }

    private sealed class CapturingHandlerFilter(Action<HttpMessageHandler> capture) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            capture(builder.PrimaryHandler);
        };
    }
}
