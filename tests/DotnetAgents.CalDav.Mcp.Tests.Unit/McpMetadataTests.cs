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
    public void McpServerJson_ContainsExpectedEnvironmentVariables()
    {
        var serverJsonPath = GetOutputFilePath(".mcp", "server.json");
        File.Exists(serverJsonPath).ShouldBeTrue(".mcp/server.json must exist for this test");

        var json = File.ReadAllText(serverJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Must have an env or environmentVariables section with the expected keys
        var envVars = root.TryGetProperty("env", out var env)
            ? env
            : root.GetProperty("environmentVariables");

        var envVarNames = envVars.EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        envVarNames.ShouldContain("CALDAV_URL");
        envVarNames.ShouldContain("CALDAV_USERNAME");
        envVarNames.ShouldContain("CALDAV_PASSWORD");
    }

    [Fact]
    public void McpServerJson_ContainsOptionalTaskListsVariable()
    {
        var serverJsonPath = GetOutputFilePath(".mcp", "server.json");
        File.Exists(serverJsonPath).ShouldBeTrue(".mcp/server.json must exist for this test");

        var json = File.ReadAllText(serverJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var envVars = root.TryGetProperty("env", out var env)
            ? env
            : root.GetProperty("environmentVariables");

        var envVarNames = envVars.EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        envVarNames.ShouldContain("CALDAV_TASK_LISTS");
    }

    [Fact]
    public void McpServerJson_ContainsOptionalDefaultTaskListVariable()
    {
        var serverJsonPath = GetOutputFilePath(".mcp", "server.json");
        File.Exists(serverJsonPath).ShouldBeTrue(".mcp/server.json must exist for this test");

        var json = File.ReadAllText(serverJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var envVars = root.TryGetProperty("env", out var env)
            ? env
            : root.GetProperty("environmentVariables");

        var envVarNames = envVars.EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        envVarNames.ShouldContain("CALDAV_DEFAULT_TASK_LIST");
    }

    // ─── packaging metadata ─────────────────────────────────────────────────────

    [Fact]
    public void McpProjectFile_ContainsKeyPackagingProperties()
    {
        var projectDir = GetMcpProjectDir();
        var csprojPath = Path.Combine(projectDir, "DotnetAgents.CalDav.Mcp.csproj");
        File.Exists(csprojPath).ShouldBeTrue("MCP project file must exist");

        var csproj = File.ReadAllText(csprojPath);

        csproj.ShouldContain("<PackageId>");
        csproj.ShouldContain("<PackageType>");
        csproj.ShouldContain("<PackAsTool>");
    }
}
