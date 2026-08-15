using System.Collections.Concurrent;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

/// <summary>Exercises the Calendar tracer bullet through the SDK's native stdio client.</summary>
[Collection("RadicaleCollection")]
public sealed class CalendarMcpStdioIntegrationTests
{
    private readonly RadicaleFixture _fixture;

    public CalendarMcpStdioIntegrationTests(RadicaleFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CalendarList_UsesNativeDiscoverAndReturnsStructuredContentOverStdio()
    {
        var stderr = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [GetServerAssemblyPath()],
            WorkingDirectory = AppContext.BaseDirectory,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = CreateEnvironment(),
            StandardErrorLines = stderr.Enqueue
        });
        var options = new McpClientOptions
        {
            ProtocolVersion = "2026-07-28",
            DiscoverProbeTimeout = TimeSpan.FromSeconds(10)
        };

        await using var client = await McpClient.CreateAsync(transport, options, cancellationToken: TestContext.Current.CancellationToken);
        var listedTools = await client.ListToolsAsync(new ListToolsRequestParams(), TestContext.Current.CancellationToken);
        var calendarTool = listedTools.Tools.Single(tool => tool.Name == "calendars.list");
        var result = await client.CallToolAsync("calendars.list", null, cancellationToken: TestContext.Current.CancellationToken);

        client.ServerInfo.ShouldNotBeNull();
        client.NegotiatedProtocolVersion.ShouldBe("2026-07-28");
        listedTools.Tools.Select(tool => tool.Name).ShouldBe(
        [
            "calendars.list",
            "list_task_lists",
            "show_tasks",
            "add_task",
            "find_tasks",
            "complete_task_by_summary",
            "delete_task_by_summary"
        ]);
        calendarTool.InputSchema.GetProperty("type").GetString().ShouldBe("object");
        calendarTool.InputSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        calendarTool.OutputSchema!.Value.GetProperty("oneOf").GetArrayLength().ShouldBe(2);
        calendarTool.Meta!["cache"]!["ttlMs"]!.GetValue<int>().ShouldBe(30000);
        calendarTool.Meta!["cache"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
        result.StructuredContent.ShouldNotBeNull();
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("outcome").GetString().ShouldBe("success");
        structured.GetProperty("pagination").GetProperty("mode").GetString().ShouldBe("non_snapshot");
        structured.GetProperty("items").GetArrayLength().ShouldBe(1);
        structured.GetProperty("items")[0].GetProperty("calendar").GetProperty("href").GetString()
            .ShouldBe($"{_fixture.BaseUrl}{_fixture.TaskListHref}");
        stderr.ShouldBeEmpty();
    }

    private Dictionary<string, string?> CreateEnvironment() => new()
    {
        ["CALDAV_URL"] = _fixture.BaseUrl,
        ["CALDAV_USERNAME"] = "caldavtest",
        ["CALDAV_PASSWORD"] = "caldavtest123",
        ["CALDAV_CALENDAR_HREFS"] = $"{_fixture.BaseUrl}{_fixture.TaskListHref}"
    };

    private static string GetServerAssemblyPath()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "src", "DotnetAgents.CalDav.Mcp", "bin", "Release", "net10.0", "DotnetAgents.CalDav.Mcp.dll");
            if (File.Exists(candidate))
                return candidate;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not locate the built MCP server assembly.");
    }
}
