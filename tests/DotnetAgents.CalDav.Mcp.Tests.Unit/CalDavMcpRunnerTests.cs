using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Mcp.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public class CalDavMcpRunnerTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void ShouldExposeAdvancedTools_ReturnsTrue_ForCaseInsensitiveTrue(string envValue)
    {
        var result = CalDavMcpRunner.ShouldExposeAdvancedTools(_ => envValue);

        result.ShouldBeTrue();
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
}
