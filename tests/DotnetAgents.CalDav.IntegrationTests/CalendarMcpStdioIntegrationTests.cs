using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
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
            "calendar_resources.get",
            "list_task_lists",
            "show_tasks",
            "add_task",
            "find_tasks",
            "complete_task_by_summary",
            "delete_task_by_summary"
        ]);
        listedTools.Tools.ShouldNotContain(tool => tool.Name == "calendar_resources.exact_get");
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

    [Fact]
    public async Task CalendarResourceGet_ReturnsTheSameStrongRevisionAndExactUtf8Payload()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\nBEGIN:VTODO\r\nUID:resource-read-1\r\nDTSTAMP:20260815T120000Z\r\nSUMMARY:Lossless integration\r\nX-UNKNOWN;X-P=one,two:kept\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var href = await PutResourceAsync("resource-read-1.ics", content);
        var observed = await GetResourceAsync(href);
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(stderr, exposeExact: false);

        var result = await client.CallToolAsync(
            "calendar_resources.get",
            new Dictionary<string, object?> { ["href"] = href },
            cancellationToken: TestContext.Current.CancellationToken);

        var snapshot = result.StructuredContent!.Value.GetProperty("snapshot");
        snapshot.GetProperty("resourceRevision").GetProperty("href").GetString().ShouldBe(href);
        snapshot.GetProperty("resourceRevision").GetProperty("entityTag").GetString().ShouldBe(observed.EntityTag);
        Convert.FromBase64String(snapshot.GetProperty("authoritativePayload").GetProperty("base64Utf8").GetString()!)
            .ShouldBe(observed.Utf8);
        snapshot.GetProperty("projection").GetProperty("kind").GetString().ShouldBe("todo");
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldNotContain("BEGIN:VCALENDAR");
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExactGet_UsesProtectedNativeResourceReadWhileListRemainsEmpty()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\nBEGIN:VTODO\r\nUID:exact-read-1\r\nDTSTAMP:20260815T120000Z\r\nSUMMARY:Exact integration\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var href = await PutResourceAsync("exact-read-1.ics", content);
        var observed = await GetResourceAsync(href);
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(stderr, exposeExact: true);

        var tools = await client.ListToolsAsync(new ListToolsRequestParams(), TestContext.Current.CancellationToken);
        var listed = await client.ListResourcesAsync(new ListResourcesRequestParams(), TestContext.Current.CancellationToken);
        var toolResult = await client.CallToolAsync(
            "calendar_resources.exact_get",
            new Dictionary<string, object?> { ["href"] = href },
            cancellationToken: TestContext.Current.CancellationToken);
        var link = toolResult.Content.OfType<ResourceLinkBlock>().Single();
        var read = await client.ReadResourceAsync(link.Uri, cancellationToken: TestContext.Current.CancellationToken);

        tools.Tools.Select(tool => tool.Name).ShouldBe(
        [
            "calendars.list",
            "calendar_resources.get",
            "calendar_resources.exact_get",
            "list_task_lists",
            "show_tasks",
            "add_task",
            "find_tasks",
            "complete_task_by_summary",
            "delete_task_by_summary"
        ]);
        listed.Resources.ShouldBeEmpty();
        read.Contents.ShouldHaveSingleItem().ShouldBeOfType<TextResourceContents>().Text
            .ShouldBe(Encoding.UTF8.GetString(observed.Utf8));

        await PutResourceAsync("exact-read-1.ics", content.Replace("Exact integration", "Changed revision", StringComparison.Ordinal));
        await Should.ThrowAsync<ModelContextProtocol.McpException>(() =>
            client.ReadResourceAsync(link.Uri, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        stderr.ShouldBeEmpty();
    }

    private async Task<McpClient> CreateClientAsync(ConcurrentQueue<string> stderr, bool exposeExact)
    {
        var environment = CreateEnvironment();
        environment["CALDAV_EXPOSE_EXACT_TOOLS"] = exposeExact ? "true" : "false";
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [GetServerAssemblyPath()],
            WorkingDirectory = AppContext.BaseDirectory,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = environment,
            StandardErrorLines = stderr.Enqueue
        });
        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ProtocolVersion = "2026-07-28", DiscoverProbeTimeout = TimeSpan.FromSeconds(10) },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<string> PutResourceAsync(string name, string content)
    {
        using var client = CreateAuthenticatedClient();
        var href = $"{_fixture.TaskListHref}{name}";
        using var response = await client.PutAsync(
            href,
            new StringContent(content, Encoding.UTF8, "text/calendar"),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return $"{_fixture.BaseUrl}{href}";
    }

    private async Task<ObservedResource> GetResourceAsync(string href)
    {
        using var client = CreateAuthenticatedClient();
        using var response = await client.GetAsync(href, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.IsWeak.ShouldBeFalse();
        return new ObservedResource(
            response.Headers.ETag.ToString(),
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("caldavtest:caldavtest123")));
        return client;
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

    private sealed record ObservedResource(string EntityTag, byte[] Utf8);
}
