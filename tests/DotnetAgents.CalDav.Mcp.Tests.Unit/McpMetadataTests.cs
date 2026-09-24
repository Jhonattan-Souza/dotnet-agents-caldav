using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            "CALDAV_AUTH_SCHEME",
            "CALDAV_USERNAME",
            "CALDAV_PASSWORD",
            "CALDAV_CALENDAR_HREFS",
            "CALDAV_DEFAULT_TODO_CALENDAR_NAME",
            "CALDAV_DEFAULT_EVENT_CALENDAR_NAME",
            "CALDAV_EVALUATION_TIME_ZONE",
            "CALDAV_INTEROPERABILITY_PROFILE",
            "CALDAV_SCHEDULING_MODE",
            "CALDAV_REDIRECT_HOSTS",
            "CALDAV_EXPOSE_EXACT_TOOLS",
            "OTEL_EXPORTER_OTLP_ENDPOINT",
            "OTEL_EXPORTER_OTLP_PROTOCOL",
            "OTEL_EXPORTER_OTLP_HEADERS",
            "OTEL_SERVICE_NAME",
            "OTEL_SDK_DISABLED"
        ]);
        var schedulingMode = envVars.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "CALDAV_SCHEDULING_MODE");
        schedulingMode.GetProperty("isRequired").GetBoolean().ShouldBeFalse();
        schedulingMode.GetProperty("description").GetString().ShouldNotBeNull().ShouldContain("server_managed");
        schedulingMode.GetProperty("default").GetString().ShouldBe("storage_only");
        schedulingMode.GetProperty("choices").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(["storage_only", "server_managed"]);
        envVars.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "OTEL_EXPORTER_OTLP_HEADERS")
            .GetProperty("isSecret").GetBoolean().ShouldBeTrue();
        var authScheme = envVars.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "CALDAV_AUTH_SCHEME");
        authScheme.GetProperty("isRequired").GetBoolean().ShouldBeFalse();
        authScheme.GetProperty("description").GetString().ShouldNotBeNull().ShouldContain("basic (default) or bearer");
        envVars.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "CALDAV_USERNAME")
            .GetProperty("isRequired").GetBoolean().ShouldBeFalse();
        var password = envVars.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "CALDAV_PASSWORD");
        password.GetProperty("isSecret").GetBoolean().ShouldBeTrue();
        password.GetProperty("description").GetString().ShouldNotBeNull().ShouldContain("token for bearer");
        envVars.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "CALDAV_EVALUATION_TIME_ZONE")
            .GetProperty("isRequired").GetBoolean().ShouldBeTrue();
        var evaluationZoneDescription = envVars.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "CALDAV_EVALUATION_TIME_ZONE")
            .GetProperty("description").GetString();
        evaluationZoneDescription.ShouldNotBeNull();
        evaluationZoneDescription.ShouldContain(
            "bounded Calendar Entity Starts and every Occurrence or To-do Start");
        evaluationZoneDescription.ShouldNotContain("later", Case.Insensitive);
        evaluationZoneDescription.ShouldNotContain("cutover", Case.Insensitive);
        var description = root.GetProperty("description").GetString()!;
        description.ShouldContain("Calendars");
        description.ShouldContain("Events");
        description.ShouldContain("To-dos");
        description.ShouldContain("free/busy");
        description.ShouldContain("collection sync");
        description.ShouldContain("metadata updates");
        description.ShouldNotContain("task management", Case.Insensitive);
    }

    // The registry schema has no protocol field, so the negotiated revisions travel as publisher-provided
    // metadata and must stay equal to the live catalog the runtime is gated against.
    [Fact]
    public void McpServerJson_PublishesTheNegotiatedProtocolRevisionsFromTheCatalog()
    {
        var projectDirectory = GetMcpProjectDir();
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, ".mcp", "server.json")));
        using var catalog = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projectDirectory, "Contracts", "mcp-tool-catalog.json")));

        var published = metadata.RootElement.GetProperty("_meta")
            .GetProperty("io.modelcontextprotocol.registry/publisher-provided")
            .GetProperty("protocolVersions").EnumerateArray().Select(item => item.GetString()).ToArray();
        var supported = catalog.RootElement.GetProperty("supportedProtocolRevisions")
            .EnumerateArray().Select(item => item.GetString()).ToArray();

        published.ShouldBe(["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25", "2026-07-28"]);
        published.ShouldBe(supported);
    }

    [Fact]
    public void McpServerJson_CalendarEnvironmentMatchesTheLiveCatalog()
    {
        var projectDirectory = GetMcpProjectDir();
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, ".mcp", "server.json")));
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, "Contracts", "mcp-tool-catalog.json")));
        var packaged = metadata.RootElement.GetProperty("packages")[0].GetProperty("environmentVariables")
            .EnumerateArray()
            .Where(item => item.GetProperty("name").GetString()!.StartsWith("CALDAV_", StringComparison.Ordinal))
            .Select(item => (
                Name: item.GetProperty("name").GetString()!,
                Required: item.GetProperty("isRequired").GetBoolean(),
                Secret: item.TryGetProperty("isSecret", out var secret) && secret.GetBoolean()))
            .ToArray();
        var live = catalog.RootElement.GetProperty("environment")
            .EnumerateArray()
            .Select(item => (
                Name: item.GetProperty("name").GetString()!,
                Required: item.GetProperty("required").GetBoolean(),
                Secret: item.TryGetProperty("secret", out var secret) && secret.GetBoolean()))
            .ToArray();

        packaged.ShouldBe(live);
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

    // ─── live tool catalog metadata ────────────────────────────────────────────

    [Fact]
    public void ToolCatalog_PrefixesToolMetaWithTheRegistryNamespace()
    {
        var projectDirectory = GetMcpProjectDir();
        using var server = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, ".mcp", "server.json")));
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, "Contracts", "mcp-tool-catalog.json")));
        var registryNamespace = server.RootElement.GetProperty("name").GetString()!.Split('/')[0];

        var cacheKey = catalog.RootElement.GetProperty("toolMetaKeys").GetProperty("cache").GetString();

        cacheKey.ShouldBe($"{registryNamespace}/cache");
        Regex.IsMatch(cacheKey!, MetaKeyPattern).ShouldBeTrue();
        cacheKey!.Split('/')[0].Split('.')[1].ShouldNotBeOneOf("modelcontextprotocol", "mcp");
    }

    [Fact]
    public void ToolCatalog_GivesEveryToolADistinctShortTitle()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(GetMcpProjectDir(), "Contracts", "mcp-tool-catalog.json")));
        var titles = catalog.RootElement.GetProperty("tools").EnumerateArray().ToDictionary(
            tool => tool.GetProperty("name").GetString()!,
            tool => tool.GetProperty("title").GetString()!);

        titles.Count.ShouldBe(27);
        titles.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(27);
        titles.Values.ShouldAllBe(title => title.Length <= 40 && Regex.IsMatch(title, TitlePattern));
        titles["calendar_resources.get"].ShouldBe("Get Calendar Resource");
        titles["calendar_resources.exact_get"].ShouldBe("Exact Get Calendar Resource");
        titles["calendar_resources.move"].ShouldBe("Move Calendar Resource");
        titles["calendar_resources.exact_move"].ShouldBe("Exact Move Calendar Resource");
        titles["calendar_occurrences.exclude"].ShouldBe("Exclude Occurrence");
        titles["calendar_occurrences.restore_exclusion"].ShouldBe("Restore Excluded Occurrence");
    }

    [Fact]
    public void ToolCatalog_ServerInstructionsRouteToExistingToolsWithinBudget()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(GetMcpProjectDir(), "Contracts", "mcp-tool-catalog.json")));
        var toolNames = catalog.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
        var instructions = catalog.RootElement.GetProperty("serverInstructions").GetString()!;

        System.Text.Encoding.UTF8.GetByteCount(instructions).ShouldBeLessThanOrEqualTo(1600);
        Regex.Matches(instructions, @"\b(?:calendars|calendar_entities|calendar_occurrences|calendar_resources|events|todos)\.[a-z_]+\b")
            .Select(match => match.Value).Where(name => !name.EndsWith('_'))
            .ShouldAllBe(name => toolNames.Contains(name));
        instructions.ShouldContain("calendar_resources.exact_*");
        foreach (var name in new[] { "calendar_occurrences.query", "todos.query", "calendar_entities.query", "calendars.list" })
            instructions.ShouldContain(name);
        instructions.ShouldContain("10 minutes after the first page");
        instructions.ShouldContain("cursor_expired");
        instructions.ShouldContain("fresh strong revision");
        instructions.ShouldContain("input_required");
        instructions.ShouldContain("requestState");
        instructions.ShouldContain("explicit absolute hrefs");
        instructions.ShouldContain("exact writes a complete caller-authored Calendar Object Resource");
    }

    private const string MetaKeyPattern =
        @"^(?:[A-Za-z](?:[A-Za-z0-9-]*[A-Za-z0-9])?\.)*[A-Za-z](?:[A-Za-z0-9-]*[A-Za-z0-9])?/[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$";

    private const string TitlePattern = @"^[A-Z][A-Za-z/-]*(?: [A-Z][A-Za-z/-]*)*$";
}

internal static class McpRegistrySchema
{
    public static JsonSchema Parse(string schemaJson) => JsonSchema.FromText(
        schemaJson,
        new BuildOptions { SchemaRegistry = new SchemaRegistry() });
}
