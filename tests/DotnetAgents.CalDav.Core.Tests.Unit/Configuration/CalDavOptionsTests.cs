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

    [Theory]
    [InlineData("https://user:pass@caldav.example.com/")]
    [InlineData("https://caldav.example.com/path?query=1")]
    [InlineData("https://caldav.example.com/path#fragment")]
    [InlineData("https://caldav.example.com/a/%2e%2e/private/")]
    [InlineData("https://caldav.example.com/a%2fprivate/")]
    [InlineData("https://caldav.example.com/a/../private/")]
    [InlineData("https://CALDAV.example.com/calendars/")]
    [InlineData("https://caldav.example.com:443/calendars/")]
    public void ValidateCalDavOptions_RejectsNoncanonicalOrCredentialBearingEndpoint(string baseUrl)
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = baseUrl,
            Username = "user",
            Password = "pass"
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(failure => failure.Contains("canonical", StringComparison.Ordinal));
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

    [Theory]
    [InlineData("")]
    [InlineData("America/Sao_Paulo ")]
    [InlineData("Eastern Standard Time")]
    [InlineData("Private/Unknown")]
    public void ValidateCalDavOptions_RejectsInvalidConfiguredEvaluationTimeZone(string evaluationTimeZone)
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            Username = "user",
            Password = "pass",
            EvaluationTimeZone = evaluationTimeZone
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(failure => failure.Contains("EvaluationTimeZone", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("UTC")]
    [InlineData("America/Sao_Paulo")]
    public void ValidateCalDavOptions_AcceptsAbsentOrIanaEvaluationTimeZone(string? evaluationTimeZone)
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            Username = "user",
            Password = "pass",
            EvaluationTimeZone = evaluationTimeZone
        });

        result.Succeeded.ShouldBeTrue();
    }
}
