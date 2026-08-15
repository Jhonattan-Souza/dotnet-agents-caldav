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
        options.TaskLists.ShouldBeNull();
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
        };

        var configure = CalDavEnvironmentMapper.MapFromEnvironment(key => envVars.GetValueOrDefault(key));
        var options = new CalDavOptions();
        configure(options);

        options.DefaultTodoCalendarName.ShouldBe("My To-dos");
        options.DefaultEventCalendarName.ShouldBe("My Events");
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

}
