using System.Collections.Concurrent;
using System.Text.Json;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

/// <summary>
/// Confirmation answers as real clients send them, through the SDK paths that resolve the elicitation,
/// and the configured confirmation policy over stdio.
/// </summary>
public sealed partial class CalendarMcpRawStdioTests
{
    // Some clients treat elicitation as yes/no consent and answer accept with an empty form. The SDK fills
    // missing fields from schema defaults on both the server (classic elicitation/create) and the client
    // (MRTR) side, so a confirm field with a default would turn that approval into a silent refusal.
    [Theory]
    [InlineData("2025-06-18", "calendar_resources.delete")]
    [InlineData("2025-11-25", "calendar_resources.delete")]
    [InlineData("2025-06-18", "todos.patch")]
    [InlineData("2025-11-25", "todos.patch")]
    public async Task RawLegacyHandshake_AcceptWithEmptyFormFailsExplicitlyWithoutWriting(
        string protocolVersion,
        string toolName)
    {
        await using var server = new DeleteServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var process = StartServer(server.BaseUrl, server.CalendarHref);
        try
        {
            await InitializeLegacyAsync(process, protocolVersion, "{\"elicitation\":{\"form\":{}}}", timeout.Token);
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\""
                + toolName
                + "\",\"arguments\":"
                + (toolName == "todos.patch"
                    ? ReplaceAllPatchArguments(server.ResourceHref)
                    : DeleteArguments(server.ResourceHref, "stdio-delete-1"))
                + "}}");
            await process.StandardInput.FlushAsync(timeout.Token);

            var elicitation = await ReadMessageAsync(
                process,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "elicitation/create",
                timeout.Token);
            var confirm = elicitation.GetProperty("params").GetProperty("requestedSchema")
                .GetProperty("properties").GetProperty("confirm");
            confirm.TryGetProperty("default", out _).ShouldBeFalse(confirm.ToString());
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = elicitation.GetProperty("id"),
                result = new { action = "accept", content = new { } }
            }));
            await process.StandardInput.FlushAsync(timeout.Token);
            var response = await ReadResponseAsync(process, 2, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            var result = response.GetProperty("result");
            result.GetProperty("isError").GetBoolean().ShouldBeTrue(result.ToString());
            AssertIncompleteAccept(result.GetProperty("structuredContent"));
            server.DeleteCount.ShouldBe(0);
            server.PutCount.ShouldBe(0);
            server.IsDeleted.ShouldBeFalse();
            (await process.StandardError.ReadToEndAsync(timeout.Token)).ShouldBeEmpty();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task CalendarResourceDelete_NativeSdkAcceptWithEmptyFormFailsExplicitlyWithoutDeleting()
    {
        await using var server = new DeleteServer();
        var stderr = new ConcurrentQueue<string>();
        var options = new McpClientOptions
        {
            ProtocolVersion = "2026-07-28",
            DiscoverProbeTimeout = TimeSpan.FromSeconds(10),
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (_, _) => ValueTask.FromResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, JsonElement>()
                })
            }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var client = await McpStdioClientFactory.ConnectAsync(
            CreateDeleteLaunch(server, stderr),
            options,
            cancellationToken: timeout.Token);

        var result = await CallDeleteAsync(client, server.ResourceHref, timeout.Token);

        result.IsError.ShouldBe(true, result.StructuredContent?.ToString());
        AssertIncompleteAccept(result.StructuredContent!.Value);
        server.DeleteCount.ShouldBe(0);
        server.IsDeleted.ShouldBeFalse();
        stderr.ShouldBeEmpty();
    }

    // A policy that skips the confirmation must not require the client to declare elicitation, and the
    // delete still sends the reviewed strong ETag as If-Match.
    [Theory]
    [InlineData("destructive-scope", "2025-11-25")]
    [InlineData("never", "2026-07-28")]
    public async Task CalendarResourceDelete_PolicySkipDeletesWithoutElicitationAndKeepsIfMatch(
        string policy,
        string protocolVersion)
    {
        await using var server = new DeleteServer();
        var stderr = new ConcurrentQueue<string>();
        var launch = McpStdioClientFactory.CreateBuiltServerLaunch(
            new Dictionary<string, string?>
            {
                ["CALDAV_URL"] = server.BaseUrl,
                ["CALDAV_USERNAME"] = "test",
                ["CALDAV_PASSWORD"] = "test",
                ["CALDAV_CALENDAR_HREFS"] = server.CalendarHref,
                ["CALDAV_CONFIRMATION_POLICY"] = policy
            },
            stderr.Enqueue);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var client = await McpStdioClientFactory.ConnectAsync(
            launch,
            new McpClientOptions { ProtocolVersion = protocolVersion, DiscoverProbeTimeout = TimeSpan.FromSeconds(10) },
            cancellationToken: timeout.Token);

        var result = await CallDeleteAsync(client, server.ResourceHref, timeout.Token);

        result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("outcome").GetString().ShouldBe("success");
        structured.GetProperty("mutationState").GetString().ShouldBe("committed");
        structured.GetProperty("confirmation").GetString().ShouldBe("skipped_by_policy");
        server.DeleteCount.ShouldBe(1);
        server.ObservedIfMatch.ShouldBe("\"r1\"");
        stderr.ShouldBeEmpty();
    }

    private static void AssertIncompleteAccept(JsonElement structured)
    {
        structured.GetProperty("code").GetString().ShouldBe("confirmation_mismatch", structured.ToString());
        structured.GetProperty("category").GetString().ShouldBe("confirmation");
        structured.GetProperty("phase").GetString().ShouldBe("mrtr");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        var message = structured.GetProperty("message").GetString().ShouldNotBeNull();
        message.ShouldContain("confirm=true");
        message.ShouldContain("nothing was changed");
    }

    private static string ReplaceAllPatchArguments(string resourceHref) => JsonSerializer.Serialize(new
    {
        snapshot = new
        {
            href = resourceHref,
            entityUid = "stdio-delete-1",
            entityKind = "todo",
            entityTag = "\"r1\""
        },
        target = new { scope = "master" },
        patch = new
        {
            collections = new object[]
            {
                new { field = "categories", operation = "replaceAll", values = new[] { "Work" } }
            }
        }
    });
}
