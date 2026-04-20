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
    public void MapFromEnvironment_MapsOptionalTaskLists()
    {
        var envVars = new Dictionary<string, string?>
        {
            ["CALDAV_URL"] = "https://caldav.example.com",
            ["CALDAV_USERNAME"] = "user",
            ["CALDAV_PASSWORD"] = "pass",
            ["CALDAV_TASK_LISTS"] = "list1,list2",
        };

        var configure = CalDavEnvironmentMapper.MapFromEnvironment(key => envVars.GetValueOrDefault(key));
        var options = new CalDavOptions();
        configure(options);

        options.TaskLists.ShouldBe("list1,list2");
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
    public void MapFromEnvironment_MissingTaskLists_DefaultsToNull()
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

        options.TaskLists.ShouldBeNull();
    }

    [Fact]
    public void MapFromEnvironment_MapsDefaultTaskList()
    {
        var envVars = new Dictionary<string, string?>
        {
            ["CALDAV_URL"] = "https://caldav.example.com",
            ["CALDAV_USERNAME"] = "user",
            ["CALDAV_PASSWORD"] = "pass",
            ["CALDAV_DEFAULT_TASK_LIST"] = "My Tasks",
        };

        var configure = CalDavEnvironmentMapper.MapFromEnvironment(key => envVars.GetValueOrDefault(key));
        var options = new CalDavOptions();
        configure(options);

        options.DefaultTaskList.ShouldBe("My Tasks");
    }

    [Fact]
    public void MapFromEnvironment_MissingDefaultTaskList_DefaultsToNull()
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

        options.DefaultTaskList.ShouldBeNull();
    }
}
