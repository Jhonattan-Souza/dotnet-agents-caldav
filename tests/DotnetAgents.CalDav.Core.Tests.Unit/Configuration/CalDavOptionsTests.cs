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

    [Theory]
    [InlineData("unverified")]
    [InlineData("Radicale-3.7.8")]
    [InlineData("radicale-3.7.8 ")]
    public void ValidateCalDavOptions_RejectsUnknownInteroperabilityProfile(string profile)
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            Username = "user",
            Password = "pass",
            InteroperabilityProfile = profile
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(failure => failure.Contains("InteroperabilityProfile", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateCalDavOptions_AcceptsVerifiedRadicaleInteroperabilityProfile()
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            Username = "user",
            Password = "pass",
            InteroperabilityProfile = CalDavInteroperabilityProfiles.Radicale_3_7_8
        });

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(CalDavSchedulingModes.StorageOnly, false)]
    [InlineData(CalDavSchedulingModes.ServerManaged, true)]
    public void ValidateCalDavOptions_AcceptsTheClosedSchedulingModes(string? mode, bool serverManaged)
    {
        var options = new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            Username = "user",
            Password = "pass",
            SchedulingMode = mode
        };

        new ValidateCalDavOptions().Validate(null, options).Succeeded.ShouldBeTrue();
        CalDavSchedulingModes.IsServerManaged(options).ShouldBe(serverManaged);
    }

    [Theory]
    [InlineData("Server_Managed")]
    [InlineData("SERVER_MANAGED")]
    [InlineData(" server_managed")]
    [InlineData("server_managed ")]
    [InlineData("server-managed")]
    [InlineData("storage-only")]
    [InlineData("auto")]
    [InlineData(" ")]
    public void ValidateCalDavOptions_RejectsEveryOtherSchedulingMode(string mode)
    {
        var options = new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            Username = "user",
            Password = "pass",
            SchedulingMode = mode
        };

        var result = new ValidateCalDavOptions().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldHaveSingleItem().ShouldBe(
            "CalDav:SchedulingMode must be 'storage_only' or 'server_managed' when specified.");
        CalDavSchedulingModes.IsServerManaged(options).ShouldBeFalse();
    }

    [Theory]
    [InlineData("*.icloud.com")]
    [InlineData("https://caldav.icloud.com")]
    [InlineData("caldav.icloud.com:443")]
    [InlineData("caldav.icloud.com/")]
    [InlineData(".com")]
    [InlineData("localhost")]
    [InlineData("192.0.2.10")]
    [InlineData("[2001:db8::1]")]
    [InlineData("caldav.icloud.com.")]
    [InlineData("caldav..icloud.com")]
    [InlineData("-caldav.icloud.com")]
    [InlineData("caldav-.icloud.com")]
    [InlineData("cal_dav.icloud.com")]
    [InlineData("caldav.ícloud.com")]
    [InlineData(".icloud.com,")]
    [InlineData(".icloud.com,,caldav.example.com")]
    [InlineData(" ")]
    public void ValidateCalDavOptions_RejectsInvalidRedirectHosts(string redirectHosts)
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.icloud.com",
            Username = "user",
            Password = "pass",
            RedirectHosts = redirectHosts
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldHaveSingleItem().ShouldContain("CalDav:RedirectHosts must be a comma-separated list");
    }

    [Fact]
    public void ValidateCalDavOptions_RedirectHostsFailureDoesNotEchoTheValue()
    {
        const string privateValue = "private-host.internal:8443";

        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.icloud.com",
            Username = "user",
            Password = "pass",
            RedirectHosts = privateValue
        });

        result.Failures.ShouldHaveSingleItem().ShouldNotContain("private-host");
    }

    [Fact]
    public void ValidateCalDavOptions_RejectsOverlongRedirectHostLabelsAndNames()
    {
        var overlongLabel = new string('a', 64) + ".example.com";
        var overlongName = string.Join('.', Enumerable.Repeat(new string('a', 63), 4)) + ".com";

        foreach (var redirectHosts in new[] { overlongLabel, overlongName })
        {
            new ValidateCalDavOptions().Validate(null, new CalDavOptions
            {
                BaseUrl = "https://caldav.icloud.com",
                Username = "user",
                Password = "pass",
                RedirectHosts = redirectHosts
            }).Failed.ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".icloud.com")]
    [InlineData("p01-caldav.icloud.com")]
    [InlineData(" P01-CalDAV.iCloud.com , .example.org ")]
    [InlineData("xn--caldav-9ya.example.com")]
    public void ValidateCalDavOptions_AcceptsAbsentOrDnsRedirectHosts(string? redirectHosts)
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.icloud.com",
            Username = "user",
            Password = "pass",
            RedirectHosts = redirectHosts
        });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ValidateCalDavOptions_RedirectHostsRequireAnHttpsBaseUrl()
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "http://caldav.example.com",
            Username = "user",
            Password = "pass",
            RedirectHosts = ".example.com"
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldHaveSingleItem().ShouldBe("CalDav:RedirectHosts requires an HTTPS CalDav:BaseUrl.");
    }

    [Fact]
    public void ValidateCalDavOptions_EmptyRedirectHostsDoNotRequireHttps()
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "http://caldav.example.com",
            Username = "user",
            Password = "pass",
            RedirectHosts = string.Empty
        });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ValidateCalDavOptions_MissingBaseUrlReportsOnlyTheBaseUrlForValidRedirectHosts()
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            Username = "user",
            Password = "pass",
            RedirectHosts = ".icloud.com"
        });

        result.Failures.ShouldHaveSingleItem().ShouldBe("CalDav:BaseUrl is required.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("basic")]
    public void ValidateCalDavOptions_OmittedOrBasicSchemeRequiresUsernameAndPassword(string? scheme)
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            AuthenticationScheme = scheme
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain("CalDav:Username is required.");
        result.Failures.ShouldContain("CalDav:Password is required.");
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("BASIC")]
    [InlineData("digest")]
    [InlineData("bearer ")]
    public void ValidateCalDavOptions_RejectsSchemeOutsideTheClosedSet(string scheme)
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            AuthenticationScheme = scheme,
            Password = "token"
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldBe(["CalDav:AuthenticationScheme must be 'basic' or 'bearer' when specified."]);
    }

    [Fact]
    public void ValidateCalDavOptions_BearerSchemeAcceptsATokenWithoutUsername()
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            AuthenticationScheme = CalDavAuthenticationSchemes.Bearer,
            Password = "eyJhbGciOiJIUzI1NiJ9.e30.ZRrHA1JJJW8opsbCGfG_HACGpVUMN_a9IV7pAx_Zmeo"
        });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ValidateCalDavOptions_BearerSchemeRejectsAUsername()
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            AuthenticationScheme = CalDavAuthenticationSchemes.Bearer,
            Username = "user",
            Password = "token"
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldBe([
            "CalDav:Username must be empty when CalDav:AuthenticationScheme is 'bearer'; the token is sent without a username."
        ]);
    }

    [Fact]
    public void ValidateCalDavOptions_BearerSchemeRequiresAToken()
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            AuthenticationScheme = CalDavAuthenticationSchemes.Bearer,
            Password = " "
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldBe([
            "CalDav:Password is required and holds the token when CalDav:AuthenticationScheme is 'bearer'."
        ]);
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("line\r\nX-Injected: 1")]
    [InlineData("tab\tseparated")]
    [InlineData("caf\u00e9")]
    [InlineData("delete\u007f")]
    public void ValidateCalDavOptions_BearerSchemeRejectsATokenThatIsNotHeaderSafe(string token)
    {
        var result = new ValidateCalDavOptions().Validate(null, new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            AuthenticationScheme = CalDavAuthenticationSchemes.Bearer,
            Password = token
        });

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldHaveSingleItem().ShouldContain("visible ASCII");
        result.Failures.ShouldAllBe(failure => !failure.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public void CalDavOptions_ToString_ReportsTheEffectiveSchemeWithoutTheToken()
    {
        var options = new CalDavOptions
        {
            BaseUrl = "https://caldav.example.com",
            AuthenticationScheme = CalDavAuthenticationSchemes.Bearer,
            Password = "static-token-secret"
        };

        options.ToString().ShouldContain("AuthenticationScheme = bearer");
        options.ToString().ShouldNotContain("static-token-secret");
        new CalDavOptions().ToString().ShouldContain("AuthenticationScheme = basic");
    }
}
