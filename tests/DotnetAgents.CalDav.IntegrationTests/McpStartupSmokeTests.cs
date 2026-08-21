using System.Collections.Concurrent;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

public sealed class McpStartupSmokeTests
{
    private const string PackageSmokeExecutableEnvironmentVariable =
        "DOTNET_AGENTS_CALDAV_PACKAGE_SMOKE_EXECUTABLE";

    [Fact]
    [Trait("Category", "PackageSmoke")]
    public async Task ConfiguredServer_InitializesAndListsDefaultToolsWithoutCalDavNetwork()
    {
        var stderr = new ConcurrentQueue<string>();
        var executable = Environment.GetEnvironmentVariable(PackageSmokeExecutableEnvironmentVariable);
        var useInstalledExecutable = !string.IsNullOrWhiteSpace(executable);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = useInstalledExecutable ? executable! : "dotnet",
            Arguments = useInstalledExecutable ? [] : [GetServerAssemblyPath()],
            WorkingDirectory = useInstalledExecutable
                ? Path.GetDirectoryName(Path.GetFullPath(executable!))
                : AppContext.BaseDirectory,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["CALDAV_URL"] = "http://127.0.0.1:1/",
                ["CALDAV_USERNAME"] = "package-smoke",
                ["CALDAV_PASSWORD"] = "package-smoke",
                ["CALDAV_CALENDAR_HREFS"] = "http://127.0.0.1:1/calendars/package-smoke/",
                ["CALDAV_EXPOSE_EXACT_TOOLS"] = "false"
            },
            StandardErrorLines = stderr.Enqueue
        });
        var options = new McpClientOptions
        {
            ProtocolVersion = "2026-07-28",
            DiscoverProbeTimeout = TimeSpan.FromSeconds(10)
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        await using (var client = await McpClient.CreateAsync(
                         transport,
                         options,
                         cancellationToken: timeout.Token))
        {
            var listedTools = await client.ListToolsAsync(new ListToolsRequestParams(), timeout.Token);

            client.NegotiatedProtocolVersion.ShouldBe("2026-07-28");
            listedTools.Tools.Select(tool => tool.Name).ShouldBe(
            [
                "calendars.list",
                "calendar_entities.query",
                "calendar_occurrences.query",
                "todos.query",
                "calendar_resources.get",
                "events.create",
                "events.patch",
                "todos.create",
                "todos.patch",
                "todos.complete",
                "calendar_occurrences.add",
                "calendar_occurrences.exclude",
                "calendar_occurrences.restore_exclusion",
                "calendar_occurrences.cancel",
                "calendar_occurrences.restore_cancellation",
                "calendar_resources.move",
                "calendar_resources.delete"
            ]);
            listedTools.Tools.ShouldNotContain(tool => tool.Name.StartsWith(
                "calendar_resources.exact_",
                StringComparison.Ordinal));
        }

        stderr.ShouldBeEmpty();
    }

    private static string GetServerAssemblyPath()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory,
                "src",
                "DotnetAgents.CalDav.Mcp",
                "bin",
                "Release",
                "net10.0",
                "DotnetAgents.CalDav.Mcp.dll");
            if (File.Exists(candidate))
                return candidate;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new FileNotFoundException("Could not locate the built MCP server assembly.");
    }
}
