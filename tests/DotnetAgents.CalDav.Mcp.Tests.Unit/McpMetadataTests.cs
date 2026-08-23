using System.IO;
using System.Text.Json;
using Json.Schema;
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
    public void McpServerJson_DeclaresCalendarAndOptionalOpenTelemetryEnvironmentVariables()
    {
        var serverJsonPath = Path.Combine(GetMcpProjectDir(), ".mcp", "server.json");
        File.Exists(serverJsonPath).ShouldBeTrue(".mcp/server.json must exist for this test");

        var json = File.ReadAllText(serverJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("$schema").GetString()
            .ShouldBe("https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json");
        root.GetProperty("name").GetString()
            .ShouldBe("io.github.jhonattan-souza/dotnet-agents-caldav");
        root.GetProperty("version").GetString().ShouldBe("0.0.0");
        root.GetProperty("packages")[0].GetProperty("version").GetString().ShouldBe("0.0.0");
        root.GetProperty("packages")[0].GetProperty("transport").GetProperty("type").GetString()
            .ShouldBe("stdio");

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
            "CALDAV_EVALUATION_TIME_ZONE",
            "CALDAV_EXPOSE_EXACT_TOOLS",
            "OTEL_EXPORTER_OTLP_ENDPOINT",
            "OTEL_EXPORTER_OTLP_PROTOCOL",
            "OTEL_EXPORTER_OTLP_HEADERS",
            "OTEL_SERVICE_NAME",
            "OTEL_SDK_DISABLED"
        ]);
        envVars.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "OTEL_EXPORTER_OTLP_HEADERS")
            .GetProperty("isSecret").GetBoolean().ShouldBeTrue();
        envVars.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "CALDAV_EVALUATION_TIME_ZONE")
            .GetProperty("description").GetString().ShouldNotBeNull()
            .ShouldContain("bounded Calendar Entity Starts");
        var description = root.GetProperty("description").GetString()!;
        description.ShouldContain("Calendars");
        description.ShouldContain("Events");
        description.ShouldContain("To-dos");
        description.ShouldNotContain("task management", Case.Insensitive);
    }

    [Fact]
    public async Task McpServerJson_IsValidAgainstThePinnedRegistrySchema()
    {
        var projectDirectory = GetMcpProjectDir();
        var cancellationToken = TestContext.Current.CancellationToken;
        var metadata = await File.ReadAllTextAsync(Path.Combine(projectDirectory, ".mcp", "server.json"), cancellationToken);
        var schemaPath = Path.GetFullPath(Path.Combine(projectDirectory, "..", "..", "contracts", "0.2.0", "mcp-server.schema.json"));
        var schema = McpRegistrySchema.Parse(await File.ReadAllTextAsync(schemaPath, cancellationToken));
        using var document = JsonDocument.Parse(metadata);

        schema.Evaluate(document.RootElement).IsValid.ShouldBeTrue();
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

internal static class McpRegistrySchema
{
    public static JsonSchema Parse(string schemaJson) => JsonSchema.FromText(
        schemaJson,
        new BuildOptions { SchemaRegistry = new SchemaRegistry() });
}
