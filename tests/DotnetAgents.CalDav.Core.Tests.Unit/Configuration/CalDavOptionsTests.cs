using DotnetAgents.CalDav.Core.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Configuration;

public class CalDavOptionsTests
{
    [Fact]
    public void CalDavOptions_ToString_RedactsPassword()
    {
        var options = new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            Username = "user",
            Password = "super-secret"
        };

        var text = options.ToString();

        text.ShouldContain("BaseUrl = https://caldav.example.com");
        text.ShouldContain("Username = user");
        text.ShouldContain("Password = ***");
        text.ShouldNotContain("super-secret");
    }

    [Fact]
    public void ValidateCalDavOptions_FailsOnUnsupportedUrlScheme()
    {
        var validator = new ValidateCalDavOptions();

        var result = validator.Validate(null, new CalDavOptions
        {
            BaseUrl = "ftp://caldav.example.com",
            Username = "user",
            Password = "pass"
        });

        result.ShouldBeOfType<ValidateOptionsResult>();
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(failure => failure.Contains("HTTP or HTTPS URL"));
    }

    [Fact]
    public void ValidateCalDavOptions_FailsOnMalformedBaseUrl()
    {
        var validator = new ValidateCalDavOptions();

        var result = validator.Validate(null, new CalDavOptions
        {
            BaseUrl = "not-a-url",
            Username = "user",
            Password = "pass"
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(failure => failure.Contains("HTTP or HTTPS URL"));
    }

    [Fact]
    public void ValidateCalDavOptions_FailsOnWhitespaceUsername()
    {
        var validator = new ValidateCalDavOptions();

        var result = validator.Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            Username = " ",
            Password = "pass"
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain("CalDav:Username is required.");
    }

    [Fact]
    public void ValidateCalDavOptions_FailsOnWhitespacePassword()
    {
        var validator = new ValidateCalDavOptions();

        var result = validator.Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            Username = "user",
            Password = " "
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain("CalDav:Password is required.");
    }

    [Fact]
    public void ValidateCalDavOptions_FailsOnNonPositiveRequestTimeout()
    {
        var validator = new ValidateCalDavOptions();

        var result = validator.Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            Username = "user",
            Password = "pass",
            RequestTimeout = TimeSpan.Zero
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain("CalDav:RequestTimeout must be positive.");
    }

    [Fact]
    public void ValidateCalDavOptions_SucceedsOnValidOptions()
    {
        var validator = new ValidateCalDavOptions();

        var result = validator.Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            Username = "user",
            Password = "pass",
            RequestTimeout = TimeSpan.FromSeconds(15)
        });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ValidateCalDavOptions_SucceedsOnHttpBaseUrl()
    {
        var validator = new ValidateCalDavOptions();

        var result = validator.Validate(null, new CalDavOptions
        {
            BaseUrl = "http://caldav.example.com",
            Username = "user",
            Password = "pass"
        });

        result.Succeeded.ShouldBeTrue();
    }
}
