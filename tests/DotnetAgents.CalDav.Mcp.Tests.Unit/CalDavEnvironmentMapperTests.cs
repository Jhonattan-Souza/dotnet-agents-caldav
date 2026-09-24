using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Mcp.Hosting;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public class CalDavEnvironmentMapperTests
{
    [Fact]
    public void MapFromEnvironment_MapsAllRequiredEnvVars()
    {
        var envVars = new Dictionary<string, string?>
        {
            ["CALDAV_URL"] = "https://caldav.example.com",
            ["CALDAV_USERNAME"] = "testuser",
            ["CALDAV_PASSWORD"] = "testpass",
        };

        var configure = CalDavEnvironmentMapper.MapFromEnvironment(key => envVars.GetValueOrDefault(key));
        var options = new CalDavOptions();
        configure(options);

        options.BaseUrl.ShouldBe("https://caldav.example.com");
        options.Username.ShouldBe("testuser");
        options.Password.ShouldBe("testpass");
    }

    [Fact]
    public void MapFromEnvironment_MapsOptionalCalendarScope()
    {
        var envVars = new Dictionary<string, string?>
        {
            ["CALDAV_URL"] = "https://caldav.example.com",
            ["CALDAV_USERNAME"] = "user",
            ["CALDAV_PASSWORD"] = "pass",
            ["CALDAV_CALENDAR_HREFS"] = "https://caldav.example.com/a/,https://caldav.example.com/b/",
        };

        var configure = CalDavEnvironmentMapper.MapFromEnvironment(key => envVars.GetValueOrDefault(key));
        var options = new CalDavOptions();
        configure(options);

        options.CalendarHrefs.ShouldBe("https://caldav.example.com/a/,https://caldav.example.com/b/");
    }

    [Fact]
    public void MapFromEnvironment_MissingRequiredVars_DefaultsToEmpty()
    {
        var configure = CalDavEnvironmentMapper.MapFromEnvironment(_ => (string?)null);
        var options = new CalDavOptions();
        configure(options);

        options.BaseUrl.ShouldBeEmpty();
        options.Username.ShouldBeEmpty();
        options.Password.ShouldBeEmpty();
    }

    [Fact]
    public void MapFromEnvironment_MissingCalendarScope_DefaultsToNull()
    {
        var envVars = new Dictionary<string, string?>
        {
            ["CALDAV_URL"] = "https://caldav.example.com",
            ["CALDAV_USERNAME"] = "user",
            ["CALDAV_PASSWORD"] = "pass",
        };

        var configure = CalDavEnvironmentMapper.MapFromEnvironment(key => envVars.GetValueOrDefault(key));
        var options = new CalDavOptions();
        configure(options);

        options.CalendarHrefs.ShouldBeNull();
    }

    [Fact]
    public void MapFromEnvironment_MapsIndependentDefaultCalendarNames()
    {
        var envVars = new Dictionary<string, string?>
        {
            ["CALDAV_URL"] = "https://caldav.example.com",
            ["CALDAV_USERNAME"] = "user",
            ["CALDAV_PASSWORD"] = "pass",
            ["CALDAV_DEFAULT_TODO_CALENDAR_NAME"] = "My To-dos",
            ["CALDAV_DEFAULT_EVENT_CALENDAR_NAME"] = "My Events",
            ["CALDAV_INTEROPERABILITY_PROFILE"] = "radicale-3.7.8",
        };

        var configure = CalDavEnvironmentMapper.MapFromEnvironment(key => envVars.GetValueOrDefault(key));
        var options = new CalDavOptions();
        configure(options);

        options.DefaultTodoCalendarName.ShouldBe("My To-dos");
        options.DefaultEventCalendarName.ShouldBe("My Events");
        options.InteroperabilityProfile.ShouldBe("radicale-3.7.8");
    }

    [Fact]
    public void MapFromEnvironment_MissingDefaultCalendarNames_DefaultToNull()
    {
        var envVars = new Dictionary<string, string?>
        {
            ["CALDAV_URL"] = "https://caldav.example.com",
            ["CALDAV_USERNAME"] = "user",
            ["CALDAV_PASSWORD"] = "pass",
        };

        var configure = CalDavEnvironmentMapper.MapFromEnvironment(key => envVars.GetValueOrDefault(key));
        var options = new CalDavOptions();
        configure(options);

        options.DefaultTodoCalendarName.ShouldBeNull();
        options.DefaultEventCalendarName.ShouldBeNull();
    }

    [Fact]
    public void MapFromEnvironment_ReadsOnlyTheFrozenCalendarEnvironmentNames()
    {
        var requestedNames = new List<string>();

        var configure = CalDavEnvironmentMapper.MapFromEnvironment(name =>
        {
            requestedNames.Add(name);
            return null;
        });
        configure(new CalDavOptions());

        requestedNames.ShouldBe(
        [
            "CALDAV_URL",
            "CALDAV_USERNAME",
            "CALDAV_PASSWORD",
            "CALDAV_OAUTH_TOKEN_ENDPOINT",
            "CALDAV_OAUTH_CLIENT_ID",
            "CALDAV_OAUTH_CLIENT_SECRET",
            "CALDAV_OAUTH_REFRESH_TOKEN",
            "CALDAV_CALENDAR_HREFS",
            "CALDAV_DEFAULT_TODO_CALENDAR_NAME",
            "CALDAV_DEFAULT_EVENT_CALENDAR_NAME",
            "CALDAV_EVALUATION_TIME_ZONE",
            "CALDAV_INTEROPERABILITY_PROFILE",
            "CALDAV_SCHEDULING_MODE",
            "CALDAV_REDIRECT_HOSTS"
        ]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("storage_only")]
    [InlineData("server_managed")]
    [InlineData("Server_Managed")]
    public void MapFromEnvironment_MapsSchedulingModeWithoutInterpretingIt(string? mode)
    {
        var configure = CalDavEnvironmentMapper.MapFromEnvironment(name =>
            name == "CALDAV_SCHEDULING_MODE" ? mode : null);
        var options = new CalDavOptions();

        configure(options);

        options.SchedulingMode.ShouldBe(mode);
    }

    [Fact]
    public void MapFromEnvironment_MapsOAuthRefreshTokenGrantSettings()
    {
        var envVars = new Dictionary<string, string?>
        {
            ["CALDAV_AUTH_SCHEME"] = "oauth2",
            ["CALDAV_OAUTH_TOKEN_ENDPOINT"] = "https://oauth2.example.com/token",
            ["CALDAV_OAUTH_CLIENT_ID"] = "client-id",
            ["CALDAV_OAUTH_CLIENT_SECRET"] = "client-secret",
            ["CALDAV_OAUTH_REFRESH_TOKEN"] = "refresh-token",
        };
        var options = new CalDavOptions();

        CalDavEnvironmentMapper.MapFromEnvironment(key => envVars.GetValueOrDefault(key))(options);

        options.AuthenticationScheme.ShouldBe("oauth2");
        options.OAuthTokenEndpoint.ShouldBe("https://oauth2.example.com/token");
        options.OAuthClientId.ShouldBe("client-id");
        options.OAuthClientSecret.ShouldBe("client-secret");
        options.OAuthRefreshToken.ShouldBe("refresh-token");
        options.Username.ShouldBeEmpty();
        options.Password.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("America/Sao_Paulo")]
    [InlineData("invalid-zone")]
    public void MapFromEnvironment_MapsConfiguredTemporalEvaluationContextExactly(string? zone)
    {
        var configure = CalDavEnvironmentMapper.MapFromEnvironment(name =>
            name == "CALDAV_EVALUATION_TIME_ZONE" ? zone : null);
        var options = new CalDavOptions();

        configure(options);

        options.EvaluationTimeZone.ShouldBe(zone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(".icloud.com")]
    [InlineData("p01-caldav.icloud.com, .example.org")]
    public void MapFromEnvironment_MapsRedirectHostAllowlistExactly(string? redirectHosts)
    {
        var configure = CalDavEnvironmentMapper.MapFromEnvironment(name =>
            name == "CALDAV_REDIRECT_HOSTS" ? redirectHosts : null);
        var options = new CalDavOptions();

        configure(options);

        options.RedirectHosts.ShouldBe(redirectHosts);
    }


    [Theory]
    [InlineData(null)]
    [InlineData("basic")]
    [InlineData("bearer")]
    [InlineData("Bearer")]
    public void MapFromEnvironment_MapsAuthenticationSchemeExactlyForStartupValidation(string? scheme)
    {
        var configure = CalDavEnvironmentMapper.MapFromEnvironment(name =>
            name == "CALDAV_AUTH_SCHEME" ? scheme : null);
        var options = new CalDavOptions();

        configure(options);

        options.AuthenticationScheme.ShouldBe(scheme);
    }
}
