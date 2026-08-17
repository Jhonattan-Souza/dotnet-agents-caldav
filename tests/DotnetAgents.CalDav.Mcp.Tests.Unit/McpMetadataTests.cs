using System.IO;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public class McpMetadataTests
{
    private static string GetOutputFilePath(params string[] relativePathSegments) =>
        relativePathSegments.Aggregate(AppContext.BaseDirectory, Path.Combine);

    private static string GetMcpProjectDir()
    {
        var assemblyDir = AppContext.BaseDirectory;
        var dir = assemblyDir;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "src", "DotnetAgents.CalDav.Mcp");
            if (Directory.Exists(candidate))
                return candidate;

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate src/DotnetAgents.CalDav.Mcp directory.");
    }

    // ─── server.json metadata ──────────────────────────────────────────────────

    [Fact]
    public void McpServerJson_ExistsInProject()
    {
        var serverJsonPath = GetOutputFilePath(".mcp", "server.json");

        File.Exists(serverJsonPath).ShouldBeTrue(
            $".mcp/server.json should exist at {serverJsonPath}");
    }

    [Fact]
    public void McpServerJson_DeclaresOnlyFrozenCalendarEnvironmentVariables()
    {
        var serverJsonPath = GetOutputFilePath(".mcp", "server.json");
        File.Exists(serverJsonPath).ShouldBeTrue(".mcp/server.json must exist for this test");

        var json = File.ReadAllText(serverJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // MCP Registry schema: environmentVariables are inside packages[0]
        var envVars = root.GetProperty("packages")[0].GetProperty("environmentVariables");

        var envVarNames = envVars.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToList();

        envVarNames.ShouldBe(
        [
            "CALDAV_URL",
            "CALDAV_USERNAME",
            "CALDAV_PASSWORD",
            "CALDAV_CALENDAR_HREFS",
            "CALDAV_DEFAULT_TODO_CALENDAR_NAME",
            "CALDAV_DEFAULT_EVENT_CALENDAR_NAME",
            "CALDAV_EXPOSE_EXACT_TOOLS"
        ]);
        var description = root.GetProperty("description").GetString()!;
        description.ShouldContain("Calendars");
        description.ShouldContain("Events");
        description.ShouldContain("To-dos");
        description.ShouldNotContain("task management", Case.Insensitive);
    }

    // ─── packaging metadata ─────────────────────────────────────────────────────

    [Fact]
    public void McpProjectFile_ContainsKeyPackagingProperties()
    {
        var projectDir = GetMcpProjectDir();
        var csprojPath = Path.Combine(projectDir, "DotnetAgents.CalDav.Mcp.csproj");
        File.Exists(csprojPath).ShouldBeTrue("MCP project file must exist");

        var csproj = File.ReadAllText(csprojPath);

        csproj.ShouldContain("<PackageId>dotnet-agents-caldav</PackageId>");
        csproj.ShouldContain("<PackageType>");
        csproj.ShouldContain("<PackAsTool>");
        csproj.ShouldContain("<ToolCommandName>dotnet-agents-caldav</ToolCommandName>");
    }
}
