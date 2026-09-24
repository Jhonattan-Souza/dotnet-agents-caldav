using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Mcp.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public class CalDavMcpRunnerTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    public void ShouldExposeExactTools_UsesIndependentOptIn(string? envValue, bool expected)
    {
        CalDavMcpRunner.ShouldExposeExactTools(_ => envValue).ShouldBe(expected);
    }

    [Fact]
    public void ShouldExposeExactTools_ReadsOnlyTheFrozenExactGateName()
    {
        var requestedNames = new List<string>();

        CalDavMcpRunner.ShouldExposeExactTools(name =>
        {
            requestedNames.Add(name);
            return null;
        }).ShouldBeFalse();

        requestedNames.ShouldBe(["CALDAV_EXPOSE_EXACT_TOOLS"]);
    }

    [Fact]
    public async Task RunAsync_WithAllConfigMissing_ReturnsExitCode1()
    {
        var sw = new StringWriter();
        var runner = new CalDavMcpRunner(sw);

        var exitCode = await runner.RunAsync(options =>
        {
            // All defaults to empty strings — invalid CalDAV config
        }, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_WithInvalidConfig_WritesHumanReadableError()
    {
        var sw = new StringWriter();
        var runner = new CalDavMcpRunner(sw);

        await runner.RunAsync(options =>
        {
            // Defaults — all required fields empty
        }, TestContext.Current.CancellationToken);

        var output = sw.ToString();
        output.ShouldContain("CalDAV configuration error");
        output.ShouldContain("BaseUrl");
    }

    [Fact]
    public async Task RunAsync_EmptyBaseUrl_ReturnsExitCode1_WithBaseUrlInError()
    {
        var sw = new StringWriter();
        var runner = new CalDavMcpRunner(sw);

        var exitCode = await runner.RunAsync(options =>
        {
            options.BaseUrl = "";
            options.Username = "user";
            options.Password = "pass";
        }, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        sw.ToString().ShouldContain("BaseUrl is required");
    }

    [Fact]
    public async Task RunAsync_InvalidBaseUrl_ReturnsExitCode1_WithUrlMessageInError()
    {
        var sw = new StringWriter();
        var runner = new CalDavMcpRunner(sw);

        var exitCode = await runner.RunAsync(options =>
        {
            options.BaseUrl = "not-a-url";
            options.Username = "user";
            options.Password = "pass";
        }, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        var output = sw.ToString();
        output.ShouldContain("CalDAV configuration error");
        output.ShouldContain("valid HTTP or HTTPS URL");
    }

    [Fact]
    public async Task RunAsync_InvalidConfiguredEvaluationTimeZoneFailsStartupWithoutEchoingTheValue()
    {
        const string privateValue = "Private/Secret-Zone";
        var output = new StringWriter();

        var exitCode = await new CalDavMcpRunner(output).RunAsync(options =>
        {
            options.BaseUrl = "https://caldav.example.com";
            options.Username = "user";
            options.Password = "pass";
            options.EvaluationTimeZone = privateValue;
        }, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("EvaluationTimeZone");
        output.ToString().ShouldNotContain(privateValue);
    }

    [Fact]
    public async Task RunAsync_UnknownSchedulingModeFailsStartupWithoutEchoingTheValue()
    {
        const string privateValue = "Server_Managed-private";
        var output = new StringWriter();

        var exitCode = await new CalDavMcpRunner(output).RunAsync(options =>
        {
            options.BaseUrl = "https://caldav.example.com";
            options.Username = "user";
            options.Password = "pass";
            options.SchedulingMode = privateValue;
        }, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("CalDav:SchedulingMode must be 'storage_only' or 'server_managed'");
        output.ToString().ShouldNotContain(privateValue);
    }

    [Fact]
    public async Task RunAsync_MissingUsername_ReturnsExitCode1_WithUsernameInError()
    {
        var sw = new StringWriter();
        var runner = new CalDavMcpRunner(sw);

        var exitCode = await runner.RunAsync(options =>
        {
            options.BaseUrl = "https://caldav.example.com";
            options.Username = "";
            options.Password = "pass";
        }, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        sw.ToString().ShouldContain("Username is required");
    }

    [Fact]
    public async Task RunAsync_MissingPassword_ReturnsExitCode1_WithPasswordInError()
    {
        var sw = new StringWriter();
        var runner = new CalDavMcpRunner(sw);

        var exitCode = await runner.RunAsync(options =>
        {
            options.BaseUrl = "https://caldav.example.com";
            options.Username = "user";
            options.Password = "";
        }, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        sw.ToString().ShouldContain("Password is required");
    }

    [Fact]
    public async Task RunAsync_BearerSchemeWithUsernameFailsStartupWithoutEchoingTheToken()
    {
        const string token = "private-bearer-token";
        var output = new StringWriter();

        var exitCode = await new CalDavMcpRunner(output).RunAsync(CalDavEnvironmentMapper.MapFromEnvironment(name => name switch
        {
            "CALDAV_URL" => "https://caldav.example.com",
            "CALDAV_AUTH_SCHEME" => "bearer",
            "CALDAV_USERNAME" => "user",
            "CALDAV_PASSWORD" => token,
            _ => null
        }), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("CalDav:Username must be empty when CalDav:AuthenticationScheme is 'bearer'");
        output.ToString().ShouldNotContain(token);
    }

    [Fact]
    public async Task RunAsync_IncompleteOAuthConfigurationFailsStartupWithoutEchoingSecrets()
    {
        var output = new StringWriter();

        var exitCode = await new CalDavMcpRunner(output).RunAsync(CalDavEnvironmentMapper.MapFromEnvironment(name => name switch
        {
            "CALDAV_URL" => "https://apidata.example.com/caldav/v2/",
            "CALDAV_AUTH_SCHEME" => "oauth2",
            "CALDAV_OAUTH_TOKEN_ENDPOINT" => "http://oauth2.example.com/token",
            "CALDAV_OAUTH_CLIENT_SECRET" => "private-client-secret",
            "CALDAV_OAUTH_REFRESH_TOKEN" => "private-refresh-token",
            _ => null
        }), TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        var text = output.ToString();
        text.ShouldContain("CalDav:OAuthTokenEndpoint is required and must be an absolute HTTPS URL");
        text.ShouldContain("CalDav:OAuthClientId is required");
        text.ShouldNotContain("private-client-secret");
        text.ShouldNotContain("private-refresh-token");
        text.ShouldNotContain("oauth2.example.com");
    }

    [Fact]
    public async Task RunAsync_UnknownAuthenticationSchemeFailsStartupWithTheClosedSet()
    {
        var output = new StringWriter();

        var exitCode = await new CalDavMcpRunner(output).RunAsync(options =>
        {
            options.BaseUrl = "https://caldav.example.com";
            options.AuthenticationScheme = "digest";
            options.Username = "user";
            options.Password = "pass";
        }, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("CalDav:AuthenticationScheme must be 'basic', 'bearer', or 'oauth2' when specified.");
    }

    [Fact]
    public async Task RunAsync_DoesNotThrowUnhandled_OptionsValidationException()
    {
        // Proves the runner catches OptionsValidationException rather than propagating it
        var sw = new StringWriter();
        var runner = new CalDavMcpRunner(sw);

        // If OptionsValidationException escaped the runner, Record.ExceptionAsync would return it
        var exception = await Record.ExceptionAsync(async () =>
        {
            await runner.RunAsync(options =>
            {
                options.BaseUrl = "";
                options.Username = "";
                options.Password = "";
            }, TestContext.Current.CancellationToken);
        });

        // No exception should escape — the runner swallows it and returns an exit code
        exception.ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_WithValidConfig_CatchesValidationErrorsNotOtherExceptions()
    {
        // The error output should contain structured validation messages, not raw stack traces
        var sw = new StringWriter();
        var runner = new CalDavMcpRunner(sw);

        await runner.RunAsync(options =>
        {
            options.BaseUrl = "";
        }, TestContext.Current.CancellationToken);

        var output = sw.ToString();
        output.ShouldNotContain("Stack trace"); // No raw exception stack traces
        output.ShouldNotContain("Exception");   // No raw exception type names
        output.ShouldContain("CalDAV configuration error"); // Clean, human-readable header
    }

    [Fact]
    public async Task RunAsync_InvalidRedirectHostsFailStartupOnErrorOutputWithoutEchoingTheValue()
    {
        const string privateValue = "https://private-host.internal:8443";
        var output = new StringWriter();

        var exitCode = await new CalDavMcpRunner(output).RunAsync(options =>
        {
            options.BaseUrl = "https://caldav.example.com";
            options.Username = "user";
            options.Password = "pass";
            options.RedirectHosts = privateValue;
        }, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("CalDAV configuration error");
        output.ToString().ShouldContain("CalDav:RedirectHosts");
        output.ToString().ShouldNotContain("private-host");
    }

    [Fact]
    public async Task RunAsync_RedirectHostsWithHttpEndpointFailStartup()
    {
        var output = new StringWriter();

        var exitCode = await new CalDavMcpRunner(output).RunAsync(options =>
        {
            options.BaseUrl = "http://caldav.example.com";
            options.Username = "user";
            options.Password = "pass";
            options.RedirectHosts = ".example.com";
        }, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("CalDav:RedirectHosts requires an HTTPS CalDav:BaseUrl.");
    }
}
