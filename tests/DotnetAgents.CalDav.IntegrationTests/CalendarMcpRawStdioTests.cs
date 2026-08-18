using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

/// <summary>Exercises raw JSON evidence that the SDK's dictionary client cannot represent.</summary>
public sealed class CalendarMcpRawStdioTests
{
    [Fact]
    public async Task LegacyInitializeToolNameIsUnknownWhileTheProtocolHandshakeRemainsSupported()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"initialize","arguments":{}}}
            """;

        var response = await InvokeRawToolProtocolAsync(request);

        response.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32602);
        response.GetProperty("error").GetProperty("message").GetString().ShouldNotBeNull()
            .ShouldContain("Unknown tool", Case.Insensitive);
    }

    [Fact]
    public async Task TasksExtensionMethodIsUnknownWithoutApplicationSpecificAsyncState()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tasks/get","params":{"taskId":"not-a-task"}}
            """;

        var response = await InvokeRawProtocolAsync(request);

        response.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
        response.ToString().ShouldNotContain("requestState");
    }

    [Fact]
    public async Task MalformedJsonUsesTheJsonRpcParseErrorChannel()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var process = StartServer("http://127.0.0.1:1", "http://127.0.0.1:1/calendars/test/");
        try
        {
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,");
            await process.StandardInput.FlushAsync(timeout.Token);
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            line.ShouldNotBeNull();
            using var document = JsonDocument.Parse(line);

            document.RootElement.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
            document.RootElement.GetProperty("id").GetInt32().ShouldBe(2);
            document.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32700);

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            (await process.StandardError.ReadToEndAsync(timeout.Token)).ShouldBeEmpty();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Theory]
    [InlineData("calendar_availability.query")]
    [InlineData("vpolls.create")]
    public async Task UnadvertisedExtensionToolNamesUseTheProtocolUnknownToolChannel(string toolName)
    {
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = toolName, arguments = new { } }
        });

        var response = await InvokeRawToolProtocolAsync(request);

        response.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32602);
        response.GetProperty("error").GetProperty("message").GetString().ShouldNotBeNull()
            .ShouldContain("Unknown tool", Case.Insensitive);
    }

    [Fact]
    public async Task RequestedProgress_UsesOnlyBoundedAggregateNotificationsAfterFiveHundredMilliseconds()
    {
        await using var server = new SlowDiscoveryServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var process = StartServer(server.BaseUrl, server.CalendarHref);
        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"raw-test\",\"version\":\"1\"}}}");
            await process.StandardInput.FlushAsync(timeout.Token);
            _ = await ReadResponseAsync(process, 1, timeout.Token);
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"_meta\":{\"progressToken\":\"progress-1\"},\"name\":\"calendars.list\",\"arguments\":{}}}");
            await process.StandardInput.FlushAsync(timeout.Token);

            var notifications = new List<JsonElement>();
            JsonElement response = default;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("method", out var method)
                    && method.GetString() == "notifications/progress")
                {
                    notifications.Add(message.Clone());
                    server.ReleaseResponse();
                }
                if (message.TryGetProperty("id", out var id) && id.GetInt32() == 2)
                {
                    response = message.Clone();
                    break;
                }
            }

            response.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeFalse();
            notifications.Count.ShouldBeInRange(1, 12);
            var progressValues = notifications
                .Select(item => item.GetProperty("params").GetProperty("progress").GetDouble())
                .ToArray();
            progressValues.Zip(progressValues.Skip(1), (left, right) => right > left)
                .ShouldAllBe(increases => increases);
            notifications.ShouldAllBe(item =>
                item.GetProperty("params").GetProperty("progressToken").GetString() == "progress-1");
            var allowedPhases = new[] { "admission", "discovery", "fetch", "filter", "expand", "reconcile" };
            notifications.Select(item => item.GetProperty("params").GetProperty("message").GetString())
                .ShouldAllBe(phase => allowedPhases.Contains(phase));
            var serialized = JsonSerializer.Serialize(notifications);
            serialized.ShouldNotContain(server.BaseUrl);
            serialized.ShouldNotContain("Entities");

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            (await process.StandardError.ReadToEndAsync(timeout.Token)).ShouldBeEmpty();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task CalendarResourceDelete_NativeSdkCompletesMrtrOverStdioWithoutDocker()
    {
        await using var server = new DeleteServer();
        var stderr = new ConcurrentQueue<string>();
        var elicitationCount = 0;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [GetServerAssemblyPath()],
            WorkingDirectory = AppContext.BaseDirectory,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["CALDAV_URL"] = server.BaseUrl,
                ["CALDAV_USERNAME"] = "test",
                ["CALDAV_PASSWORD"] = "test",
                ["CALDAV_CALENDAR_HREFS"] = server.CalendarHref
            },
            StandardErrorLines = stderr.Enqueue
        });
        var options = new McpClientOptions
        {
            ProtocolVersion = "2026-07-28",
            DiscoverProbeTimeout = TimeSpan.FromSeconds(10),
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    Interlocked.Increment(ref elicitationCount);
                    var schema = request.ShouldNotBeNull().RequestedSchema.ShouldNotBeNull();
                    schema.Properties.ShouldNotBeNull().ShouldContainKey("confirm");
                    return ValueTask.FromResult(new ElicitResult
                    {
                        Action = "accept",
                        Content = new Dictionary<string, JsonElement>
                        {
                            ["confirm"] = JsonSerializer.SerializeToElement(true)
                        }
                    });
                }
            }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(transport, options, cancellationToken: timeout.Token);

        var result = await client.CallToolAsync(
            "calendar_resources.delete",
            new Dictionary<string, object?>
            {
                ["revision"] = new Dictionary<string, object?>
                {
                    ["href"] = server.ResourceHref,
                    ["entityUid"] = "stdio-delete-1",
                    ["entityKind"] = "todo",
                    ["entityTag"] = "\"r1\""
                }
            },
            cancellationToken: timeout.Token);

        result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("outcome").GetString().ShouldBe("success");
        structured.GetProperty("mutationState").GetString().ShouldBe("committed");
        structured.GetProperty("deletionReceipt").GetProperty("consumedEntityTag").GetString().ShouldBe("\"r1\"");
        elicitationCount.ShouldBe(1);
        server.DeleteCount.ShouldBe(1);
        server.ObservedIfMatch.ShouldBe("\"r1\"");
        server.IsDeleted.ShouldBeTrue();
        JsonSerializer.Serialize(result).ShouldNotContain("Private reviewed delete");
        stderr.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("cancel")]
    public async Task CalendarResourceDelete_NativeSdkDeclineOrCancelMakesNoDelete(string action)
    {
        await using var server = new DeleteServer();
        var stderr = new ConcurrentQueue<string>();
        var transport = CreateDeleteTransport(server, stderr);
        var options = new McpClientOptions
        {
            ProtocolVersion = "2026-07-28",
            DiscoverProbeTimeout = TimeSpan.FromSeconds(10),
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (_, _) => ValueTask.FromResult(new ElicitResult { Action = action })
            }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(transport, options, cancellationToken: timeout.Token);

        var result = await CallDeleteAsync(client, server.ResourceHref, timeout.Token);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("confirmation_declined");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        server.DeleteCount.ShouldBe(0);
        server.IsDeleted.ShouldBeFalse();
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task CalendarResourceDelete_LegacySdkIsRejectedAtProtocolHandshakeBeforeDelete()
    {
        await using var server = new DeleteServer();
        var stderr = new ConcurrentQueue<string>();
        var transport = CreateDeleteTransport(server, stderr);
        var options = new McpClientOptions
        {
            ProtocolVersion = "2025-06-18",
            DiscoverProbeTimeout = TimeSpan.FromSeconds(10)
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await Should.ThrowAsync<UnsupportedProtocolVersionException>(async () =>
        {
            await using var client = await McpClient.CreateAsync(transport, options, cancellationToken: timeout.Token);
        });
        server.DeleteCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("changed_args")]
    [InlineData("tampered_state")]
    public async Task CalendarResourceDelete_RawMrtrRejectsChangedArgumentsOrTamperedState(string mismatch)
    {
        await using var server = new DeleteServer();

        var result = await InvokeRawDeleteMrtrMismatchAsync(server, mismatch);

        AssertTypedError(result, "confirmation_mismatch", "mrtr");
        result.GetProperty("structuredContent").GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        server.DeleteCount.ShouldBe(0);
        server.IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public async Task CalendarResourceDelete_RootDuplicateRevisionReturnsTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_resources.delete","arguments":{"revision":{"href":"https://cal.example/tasks/a.ics","entityUid":"private-1","entityKind":"todo","entityTag":"\"r1\""},"revision":{"href":"https://cal.example/tasks/b.ics","entityUid":"private-2","entityKind":"todo","entityTag":"\"r2\""}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
        result.ToString().ShouldNotContain("private-1");
        result.ToString().ShouldNotContain("private-2");
    }

    [Fact]
    public async Task CalendarResourceMove_RootDuplicateDestinationReturnsTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_resources.move","arguments":{"revision":{"href":"https://cal.example/tasks/a.ics","entityUid":"private-move","entityKind":"todo","entityTag":"\"r1\""},"destination":{"mode":"default"},"destination":{"mode":"selected","calendar":{"by":"href","href":"https://cal.example/archive/"}}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
        result.GetProperty("structuredContent").GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        result.ToString().ShouldNotContain("private-move");
    }

    [Fact]
    public async Task CalendarEntityPatch_RawStdioRejectsOverrideReconciliationWithoutRequiredKindBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"events.patch","arguments":{"snapshot":{"href":"https://cal.example/events/event-1.ics","entityUid":"event-1","entityKind":"event","entityTag":"\"r1\""},"target":{"scope":"entire-set"},"patch":{"scalars":[{"field":"recurrenceSet","operation":"set","value":{"rrule":"FREQ=DAILY;COUNT=2"},"orphanReconciliations":[{"kind":"override","recurrenceIdentity":{"value":{"kind":"utcDateTime","value":"2026-08-22T10:00:00Z"}},"disposition":"remove"}]}]}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
        result.GetProperty("structuredContent").GetProperty("mutationState").GetString().ShouldBe("not_attempted");
    }

    [Theory]
    [InlineData(262_144, "invalid_input", "schemaLexicalDiscriminator")]
    [InlineData(262_145, "payload_too_large", "admissionAndPayload")]
    public async Task CalendarResourceDelete_EnforcesExact256KiBArgumentBoundary(
        int argumentBytes,
        string expectedCode,
        string expectedPhase)
    {
        var arguments = DeleteArgumentsAtSize(argumentBytes);
        Encoding.UTF8.GetByteCount(arguments).ShouldBe(argumentBytes);
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"calendar_resources.delete\",\"arguments\":"
            + arguments
            + "}}";

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, expectedCode, expectedPhase);
    }

    [Theory]
    [InlineData(262_144, "invalid_input", "schemaLexicalDiscriminator")]
    [InlineData(262_145, "payload_too_large", "admissionAndPayload")]
    public async Task CalendarResourceMove_EnforcesExact256KiBArgumentBoundary(
        int argumentBytes,
        string expectedCode,
        string expectedPhase)
    {
        var arguments = MoveArgumentsAtSize(argumentBytes);
        Encoding.UTF8.GetByteCount(arguments).ShouldBe(argumentBytes);
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"calendar_resources.move\",\"arguments\":"
            + arguments
            + "}}";

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, expectedCode, expectedPhase);
        result.GetProperty("structuredContent").GetProperty("mutationState").GetString().ShouldBe("not_attempted");
    }

    [Theory]
    [InlineData("events.create", "event", "private-event-marker")]
    [InlineData("todos.create", "todo", "private-todo-marker")]
    public async Task CalendarEntityCreate_AmbiguousPutIsReconciledOnceAndRedactedOverRawStdio(
        string toolName,
        string entityKind,
        string privateMarker)
    {
        await using var server = new AmbiguousCreateServer();
        var fields = entityKind == "event"
            ? "{\"summary\":\"" + privateMarker
                + "\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"}}"
            : "{\"summary\":\"" + privateMarker + "\"}";
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\""
            + toolName
            + "\",\"arguments\":{\"destination\":{\"mode\":\"selected\",\"calendar\":{\"by\":\"href\",\"href\":\""
            + server.CalendarHref
            + "\"}},\"entity\":{\"kind\":\""
            + entityKind
            + "\",\"uid\":\"ambiguous-"
            + entityKind
            + "\",\"fields\":"
            + fields
            + "}}}}";

        var result = await InvokeRawAsync(request, server.BaseUrl, server.CalendarHref);

        AssertTypedError(result, "indeterminate", "postWriteVerificationOrReconciliation");
        result.GetProperty("structuredContent").GetProperty("mutationState").GetString().ShouldBe("unknown");
        result.ToString().ShouldNotContain(privateMarker);
        server.PutCount.ShouldBe(1);
        server.GetCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("events.create", "{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\"event\",\"fields\":{\"summary\":\"private-event-marker\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"}}}}", "private-event-marker")]
    [InlineData("todos.create", "{\"destination\":{\"mode\":\"default\"},\"entity\":{\"kind\":\"todo\",\"fields\":{\"summary\":\"private-todo-marker\"}}}", "private-todo-marker")]
    public async Task CalendarEntityCreate_DiscoveryFailureReturnsTypedSelectionFailureWithoutWriting(
        string toolName,
        string arguments,
        string privateMarker)
    {
        await using var server = new AmbiguousCreateServer(failDiscovery: true);
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\""
            + toolName
            + "\",\"arguments\":"
            + arguments
            + "}}";

        var result = await InvokeRawAsync(request, server.BaseUrl, server.CalendarHref);

        AssertTypedError(result, "upstream_unavailable", "selectionDiscoveryCapability");
        result.GetProperty("structuredContent").GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        result.ToString().ShouldNotContain(privateMarker);
        result.ToString().ShouldNotContain("private-upstream-marker");
        server.PutCount.ShouldBe(0);
    }

    [Fact]
    public async Task EventCreate_RootDuplicateArgumentsReturnTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"events.create","arguments":{"destination":{"mode":"default"},"destination":{"mode":"selected","calendar":{"by":"name","name":"Secret"}},"entity":{"kind":"event","fields":{}}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
        result.ToString().ShouldNotContain("Secret");
    }

    [Fact]
    public async Task EventCreate_MalformedRecurrenceInputReturnsTypedInvalidInputWithoutEchoingContent()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"events.create","arguments":{"destination":{"mode":"default"},"entity":{"kind":"event","fields":{"description":"private-marker","recurrenceSet":{"rrule":null}}}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
        result.ToString().ShouldNotContain("private-marker");
    }

    [Fact]
    public async Task EventCreate_DuplicateRruleReturnsTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"events.create","arguments":{"destination":{"mode":"default"},"entity":{"kind":"event","fields":{"start":{"kind":"utcDateTime","value":"2026-08-17T13:00:00Z"},"recurrenceSet":{"rrule":"FREQ=DAILY","rrule":"FREQ=WEEKLY"}}}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
    }

    [Theory]
    [InlineData("{\"kind\":\"period\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T13:00:00Z\"}}")]
    [InlineData("{\"kind\":\"period\",\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T13:00:00Z\"},\"end\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T14:00:00Z\"},\"duration\":\"PT1H\"}")]
    public async Task EventCreate_MalformedRdatePeriodReturnsTypedInvalidInputBeforeNetwork(string period)
    {
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{" +
            "\"name\":\"events.create\",\"arguments\":{\"destination\":{\"mode\":\"default\"}," +
            "\"entity\":{\"kind\":\"event\",\"fields\":{\"start\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T13:00:00Z\"}," +
            "\"recurrenceSet\":{\"rdates\":[" + period + "]}}}}}}";

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
    }

    [Fact]
    public async Task CalendarOccurrenceQuery_NormalRawCallReachesServiceAndReturnsTypedExecutionFailure()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_occurrences.query","arguments":{"scope":{"mode":"default"},"from":{"kind":"utcDateTime","value":"2026-08-15T12:00:00Z"},"to":{"kind":"utcDateTime","value":"2026-08-16T12:00:00Z"}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "upstream_unavailable", "execution");
    }

    [Fact]
    public async Task CalendarOccurrenceQuery_RootDuplicateArgumentsReturnTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_occurrences.query","arguments":{"scope":{"mode":"default"},"scope":{"mode":"all"},"from":{"kind":"utcDateTime","value":"2026-08-15T12:00:00Z"},"to":{"kind":"utcDateTime","value":"2026-08-16T12:00:00Z"}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
    }

    [Fact]
    public async Task CalendarOccurrenceMutation_RootDuplicateArgumentsReturnRedactedTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_occurrences.cancel","arguments":{"snapshot":{"href":"https://cal.example/tasks/private-a.ics","entityUid":"private-a","entityKind":"todo","entityTag":"\"r1\""},"snapshot":{"href":"https://cal.example/tasks/private-b.ics","entityUid":"private-b","entityKind":"todo","entityTag":"\"r2\""},"recurrenceIdentity":{"value":{"kind":"utcDateTime","value":"2026-08-19T09:00:00Z"}}}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
        result.ToString().ShouldNotContain("private-a");
        result.ToString().ShouldNotContain("private-b");
    }

    [Theory]
    [InlineData("\"completedAt\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T12:00:00Z\"}")]
    [InlineData("\"scope\":\"this-and-future\"")]
    [InlineData("\"scope\":\"entire-set\"")]
    public async Task TodoCompletion_RejectsCallerTimeAndBroadScopesOverRawStdio(string forbiddenMember)
    {
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{"
            + "\"name\":\"todos.complete\",\"arguments\":{"
            + "\"snapshot\":{\"href\":\"https://cal.example/tasks/private-a.ics\","
            + "\"entityUid\":\"private-a\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"r1\\\"\"},"
            + forbiddenMember
            + "}}}";

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
        result.ToString().ShouldNotContain("private-a");
    }

    [Fact]
    public async Task CalendarOccurrenceMutation_OversizedArgumentsReturnRedactedAdmissionFailureBeforeNetwork()
    {
        var marker = new string('x', CalendarOccurrenceMutationTools.MaximumArgumentBytes);
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{"
            + "\"name\":\"calendar_occurrences.exclude\",\"arguments\":{"
            + "\"snapshot\":{\"href\":\"https://cal.example/tasks/a.ics\",\"entityUid\":\"a\","
            + "\"entityKind\":\"todo\",\"entityTag\":\"\\\"r1\\\"\"},"
            + "\"recurrenceIdentity\":{\"value\":{\"kind\":\"utcDateTime\","
            + "\"value\":\"2026-08-19T09:00:00Z\"}},\"privatePadding\":\""
            + marker
            + "\"}}}";

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "payload_too_large", "admissionAndPayload");
        result.ToString().ShouldNotContain(marker);
    }

    [Fact]
    public async Task CalendarEntityQuery_NormalInvalidArgumentsReturnTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_entities.query","arguments":{"scope":{"mode":"default"},"entityKinds":["event"],"unknown":true}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
    }

    [Fact]
    public async Task CalendarEntityQuery_SelectedNameWithInternalSpacesReachesTheService()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_entities.query","arguments":{"scope":{"mode":"selected","calendar":{"by":"name","name":"No such authorized calendar"}},"entityKinds":["todo"]}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "upstream_unavailable", "execution");
    }

    [Fact]
    public async Task CalendarEntityQuery_RootDuplicateArgumentsReturnTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_entities.query","arguments":{"scope":{"mode":"default"},"scope":{"mode":"all"},"entityKinds":["event"]}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
    }

    [Fact]
    public async Task CalendarEntityQuery_OversizedDuplicateArgumentsPreferPayloadAdmissionFailure()
    {
        var padding = new string('x', CalendarEntityTools.MaximumArgumentBytes);
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"calendar_entities.query\",\"arguments\":{\"scope\":{\"mode\":\"default\"},\"scope\":{\"mode\":\"all\"},\"entityKinds\":[\"event\"],\"padding\":\""
            + padding
            + "\"}}}";

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "payload_too_large", "admissionAndPayload");
    }

    [Fact]
    public async Task ExactCreate_InvalidSurrogateEscapeReturnsTypedInvalidInputOverRawStdio()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_resources.exact_create","arguments":{"destinationHref":"https://cal.example/events/a.ics","utf8Resource":"\uD800"}}}
            """;

        var result = await InvokeRawAsync(request, exposeExact: true);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("invalid-surrogate")]
    public async Task ExactGet_MalformedRawInputReturnsTypedInvalidInputWithoutNetwork(string scenario)
    {
        await using var server = new AmbiguousCreateServer();
        var arguments = scenario == "duplicate"
            ? "{\"href\":\"https://cal.example/events/a.ics\",\"href\":\"https://cal.example/events/b.ics\"}"
            : "{\"href\":\"\\uD800\"}";
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"calendar_resources.exact_get\",\"arguments\":"
            + arguments + "}}";

        var result = await InvokeRawAsync(request, server.BaseUrl, server.CalendarHref, exposeExact: true);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
        server.RequestCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ExactGet_EnforcesRawArgumentBoundaryBeforeNetwork(int extraByte)
    {
        await using var server = new AmbiguousCreateServer();
        var arguments = ExactGetArgumentsAtSize(CalendarQueryToolSupport.MaximumArgumentBytes + extraByte);
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"calendar_resources.exact_get\",\"arguments\":"
            + arguments + "}}";

        var result = await InvokeRawAsync(request, server.BaseUrl, server.CalendarHref, exposeExact: true);

        AssertTypedError(
            result,
            extraByte == 0 ? "invalid_input" : "payload_too_large",
            extraByte == 0 ? "schemaLexicalDiscriminator" : "admissionAndPayload");
        server.RequestCount.ShouldBe(0);
    }

    private static async Task<JsonElement> InvokeRawAsync(
        string toolRequest,
        string baseUrl = "http://127.0.0.1:1",
        string calendarHref = "http://127.0.0.1:1/calendars/test/",
        bool exposeExact = false)
    {
        var response = await InvokeRawToolProtocolAsync(toolRequest, baseUrl, calendarHref, exposeExact);
        response.TryGetProperty("result", out var result).ShouldBeTrue(response.ToString());
        return result.Clone();
    }

    private static async Task<JsonElement> InvokeRawToolProtocolAsync(
        string toolRequest,
        string baseUrl = "http://127.0.0.1:1",
        string calendarHref = "http://127.0.0.1:1/calendars/test/",
        bool exposeExact = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var process = StartServer(baseUrl, calendarHref, exposeExact);
        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"raw-test\",\"version\":\"1\"}}}");
            await process.StandardInput.FlushAsync(timeout.Token);
            _ = await ReadResponseAsync(process, 1, timeout.Token);
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.WriteLineAsync(toolRequest);
            await process.StandardInput.FlushAsync(timeout.Token);
            var response = await ReadResponseAsync(process, 2, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            (await process.StandardError.ReadToEndAsync(timeout.Token)).ShouldBeEmpty();
            return response.Clone();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static async Task<JsonElement> InvokeRawProtocolAsync(string request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var process = StartServer("http://127.0.0.1:1", "http://127.0.0.1:1/calendars/test/");
        try
        {
            await process.StandardInput.WriteLineAsync(request);
            await process.StandardInput.FlushAsync(timeout.Token);
            var response = await ReadResponseAsync(process, 2, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            (await process.StandardError.ReadToEndAsync(timeout.Token)).ShouldBeEmpty();
            return response.Clone();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static async Task<JsonElement> InvokeRawDeleteMrtrMismatchAsync(
        DeleteServer server,
        string mismatch)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var process = StartServer(server.BaseUrl, server.CalendarHref);
        var arguments = DeleteArguments(server.ResourceHref, "stdio-delete-1");
        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"raw-test\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":{}},\"name\":\"calendar_resources.delete\",\"arguments\":"
                + arguments
                + "}}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var first = await ReadResponseAsync(process, 2, timeout.Token);
            first.TryGetProperty("result", out var inputRequired).ShouldBeTrue(first.ToString());
            inputRequired.TryGetProperty("inputRequests", out var inputRequests)
                .ShouldBeTrue(inputRequired.ToString());
            inputRequests.TryGetProperty("confirm_delete", out _).ShouldBeTrue();
            var state = inputRequired.GetProperty("requestState").GetString();
            state.ShouldNotBeNullOrWhiteSpace();
            if (mismatch == "tampered_state")
                state = $"{state![..^1]}{(state[^1] == 'A' ? 'B' : 'A')}";
            else
                arguments = DeleteArguments(server.ResourceHref, "changed-uid");
            var retry = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    _meta = new Dictionary<string, object?>
                    {
                        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                        ["io.modelcontextprotocol/clientInfo"] = new { name = "raw-test", version = "1" },
                        ["io.modelcontextprotocol/clientCapabilities"] = new { }
                    },
                    name = "calendar_resources.delete",
                    arguments = JsonSerializer.Deserialize<JsonElement>(arguments),
                    requestState = state,
                    inputResponses = new Dictionary<string, object?>
                    {
                        ["confirm_delete"] = new
                        {
                            action = "accept",
                            content = new { confirm = true }
                        }
                    }
                }
            });
            await process.StandardInput.WriteLineAsync(retry);
            await process.StandardInput.FlushAsync(timeout.Token);
            var response = await ReadResponseAsync(process, 3, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            (await process.StandardError.ReadToEndAsync(timeout.Token)).ShouldBeEmpty();
            return response.GetProperty("result").Clone();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static StdioClientTransport CreateDeleteTransport(
        DeleteServer server,
        ConcurrentQueue<string> stderr) => new(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [GetServerAssemblyPath()],
            WorkingDirectory = AppContext.BaseDirectory,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["CALDAV_URL"] = server.BaseUrl,
                ["CALDAV_USERNAME"] = "test",
                ["CALDAV_PASSWORD"] = "test",
                ["CALDAV_CALENDAR_HREFS"] = server.CalendarHref
            },
            StandardErrorLines = stderr.Enqueue
        });

    private static Task<CallToolResult> CallDeleteAsync(
        McpClient client,
        string resourceHref,
        CancellationToken cancellationToken) => client.CallToolAsync(
            "calendar_resources.delete",
            new Dictionary<string, object?>
            {
                ["revision"] = new Dictionary<string, object?>
                {
                    ["href"] = resourceHref,
                    ["entityUid"] = "stdio-delete-1",
                    ["entityKind"] = "todo",
                    ["entityTag"] = "\"r1\""
                }
            },
            cancellationToken: cancellationToken).AsTask();

    private static string DeleteArguments(string resourceHref, string uid) => JsonSerializer.Serialize(new
    {
        revision = new
        {
            href = resourceHref,
            entityUid = uid,
            entityKind = "todo",
            entityTag = "\"r1\""
        }
    });

    private static string DeleteArgumentsAtSize(int argumentBytes)
    {
        var value = new Dictionary<string, object?>
        {
            ["revision"] = new
            {
                href = "https://cal.example/tasks/a.ics",
                entityUid = "todo-1",
                entityKind = "todo",
                entityTag = "\"r1\""
            },
            ["padding"] = string.Empty
        };
        var fixedBytes = JsonSerializer.SerializeToUtf8Bytes(value).Length;
        value["padding"] = new string('x', argumentBytes - fixedBytes);
        return JsonSerializer.Serialize(value);
    }

    private static string MoveArgumentsAtSize(int argumentBytes)
    {
        var value = new Dictionary<string, object?>
        {
            ["revision"] = new
            {
                href = "https://cal.example/tasks/a.ics",
                entityUid = "todo-1",
                entityKind = "todo",
                entityTag = "\"r1\""
            },
            ["destination"] = new { mode = "default" },
            ["padding"] = string.Empty
        };
        var fixedBytes = JsonSerializer.SerializeToUtf8Bytes(value).Length;
        value["padding"] = new string('x', argumentBytes - fixedBytes);
        return JsonSerializer.Serialize(value);
    }

    private static string ExactGetArgumentsAtSize(int argumentBytes)
    {
        var value = new Dictionary<string, object?>
        {
            ["href"] = "https://cal.example/events/a.ics",
            ["padding"] = string.Empty
        };
        var fixedBytes = JsonSerializer.SerializeToUtf8Bytes(value).Length;
        value["padding"] = new string('x', argumentBytes - fixedBytes);
        return JsonSerializer.Serialize(value);
    }

    private static Process StartServer(string baseUrl, string calendarHref, bool exposeExact = false)
    {
        var startInfo = new ProcessStartInfo("dotnet", GetServerAssemblyPath())
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["CALDAV_URL"] = baseUrl;
        startInfo.Environment["CALDAV_USERNAME"] = "test";
        startInfo.Environment["CALDAV_PASSWORD"] = "test";
        startInfo.Environment["CALDAV_CALENDAR_HREFS"] = calendarHref;
        startInfo.Environment["CALDAV_EXPOSE_EXACT_TOOLS"] = exposeExact ? "true" : "false";
        return Process.Start(startInfo)!;
    }

    private static async Task<JsonElement> ReadResponseAsync(
        Process process,
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == expectedId)
                return document.RootElement.Clone();
        }
        throw new InvalidOperationException("The MCP server closed stdout before returning the expected response.");
    }

    private static void AssertTypedError(JsonElement result, string code, string phase)
    {
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().ShouldBe(code);
        structured.GetProperty("phase").GetString().ShouldBe(phase);
        structured.TryGetProperty("items", out _).ShouldBeFalse();
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

    private sealed class AmbiguousCreateServer : IAsyncDisposable
    {
        private readonly bool _failDiscovery;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serve;
        private int _getCount;
        private int _putCount;
        private int _requestCount;

        public AmbiguousCreateServer(bool failDiscovery = false)
        {
            _failDiscovery = failDiscovery;
            var port = ReservePort();
            BaseUrl = $"http://127.0.0.1:{port}/";
            CalendarHref = BaseUrl + "calendars/test/entities/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }

        public string CalendarHref { get; }

        public int GetCount => Volatile.Read(ref _getCount);

        public int PutCount => Volatile.Read(ref _putCount);

        public int RequestCount => Volatile.Read(ref _requestCount);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            finally
            {
                _listener.Close();
                _stopping.Dispose();
            }
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    await RespondAsync(context);
                }
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            Interlocked.Increment(ref _requestCount);
            var method = context.Request.HttpMethod;
            if (method == "PROPFIND")
            {
                if (_failDiscovery)
                {
                    var bytes = Encoding.UTF8.GetBytes("private-upstream-marker");
                    context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
                    context.Response.Close();
                    return;
                }
                var discovery = context.Request.Url!.AbsolutePath == "/"
                    ? "<d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>/calendars/test/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                    : "<d:response><d:href>/calendars/test/entities/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><d:displayname>Entities</d:displayname><c:supported-calendar-component-set><c:comp name=\"VEVENT\"/><c:comp name=\"VTODO\"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
                await WriteXmlAsync(context.Response, discovery);
                return;
            }
            if (method == "REPORT")
            {
                await WriteXmlAsync(context.Response, string.Empty);
                return;
            }
            if (method == "PUT")
            {
                Interlocked.Increment(ref _putCount);
                context.Response.StatusCode = (int)HttpStatusCode.RequestTimeout;
                context.Response.Close();
                return;
            }
            if (method == "GET")
            {
                Interlocked.Increment(ref _getCount);
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }
            context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            context.Response.Close();
        }

        private static async Task WriteXmlAsync(HttpListenerResponse response, string body)
        {
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
                + body
                + "</d:multistatus>";
            var bytes = Encoding.UTF8.GetBytes(xml);
            response.StatusCode = (int)HttpStatusCode.MultiStatus;
            response.ContentType = "application/xml; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
            response.Close();
        }

        private static int ReservePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class SlowDiscoveryServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly TaskCompletionSource _releaseResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serve;

        public SlowDiscoveryServer()
        {
            var port = ReservePort();
            BaseUrl = $"http://127.0.0.1:{port}/";
            CalendarHref = BaseUrl + "calendars/test/entities/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }
        public string CalendarHref { get; }

        public void ReleaseResponse() => _releaseResponse.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            finally
            {
                _listener.Close();
                _stopping.Dispose();
            }
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    await _releaseResponse.Task.WaitAsync(_stopping.Token);
                    var body = context.Request.Url!.AbsolutePath == "/"
                        ? "<d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>/calendars/test/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                        : "<d:response><d:href>/calendars/test/entities/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><d:displayname>Entities</d:displayname><c:supported-calendar-component-set><c:comp name=\"VEVENT\"/><c:comp name=\"VTODO\"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
                    await WriteXmlAsync(context.Response, body);
                }
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                System.Diagnostics.Debug.Assert(_stopping.IsCancellationRequested);
            }
        }

        private static async Task WriteXmlAsync(HttpListenerResponse response, string body)
        {
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
                + body
                + "</d:multistatus>";
            var bytes = Encoding.UTF8.GetBytes(xml);
            response.StatusCode = (int)HttpStatusCode.MultiStatus;
            response.ContentType = "application/xml; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
            response.Close();
        }

        private static int ReservePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class DeleteServer : IAsyncDisposable
    {
        private const string ResourcePath = "/calendars/test/entities/stdio-delete-1.ics";
        private const string Content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\nBEGIN:VTODO\r\nUID:stdio-delete-1\r\nDTSTAMP:20260815T120000Z\r\nSUMMARY:Private reviewed delete\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serve;
        private int _deleteCount;
        private int _deleted;
        private string? _observedIfMatch;

        public DeleteServer()
        {
            var port = ReservePort();
            BaseUrl = $"http://127.0.0.1:{port}/";
            CalendarHref = BaseUrl + "calendars/test/entities/";
            ResourceHref = BaseUrl + ResourcePath.TrimStart('/');
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }

        public string CalendarHref { get; }

        public string ResourceHref { get; }

        public int DeleteCount => Volatile.Read(ref _deleteCount);

        public bool IsDeleted => Volatile.Read(ref _deleted) != 0;

        public string? ObservedIfMatch => _observedIfMatch;

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            finally
            {
                _listener.Close();
                _stopping.Dispose();
            }
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    await RespondAsync(context);
                }
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            switch (context.Request.HttpMethod)
            {
                case "PROPFIND":
                    await WriteDiscoveryAsync(context.Response, context.Request.Url!.AbsolutePath);
                    return;
                case "GET":
                    await GetAsync(context.Response);
                    return;
                case "DELETE":
                    _observedIfMatch = context.Request.Headers["If-Match"];
                    Interlocked.Increment(ref _deleteCount);
                    Interlocked.Exchange(ref _deleted, 1);
                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    context.Response.Close();
                    return;
                default:
                    context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    context.Response.Close();
                    return;
            }
        }

        private static async Task WriteDiscoveryAsync(HttpListenerResponse response, string path)
        {
            var body = path == "/"
                ? "<d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>/calendars/test/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                : "<d:response><d:href>/calendars/test/entities/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><d:displayname>Entities</d:displayname><c:supported-calendar-component-set><c:comp name=\"VEVENT\"/><c:comp name=\"VTODO\"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
                + body
                + "</d:multistatus>";
            var bytes = Encoding.UTF8.GetBytes(xml);
            response.StatusCode = (int)HttpStatusCode.MultiStatus;
            response.ContentType = "application/xml; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
            response.Close();
        }

        private async Task GetAsync(HttpListenerResponse response)
        {
            if (IsDeleted)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(Content);
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/calendar; charset=utf-8";
            response.AddHeader("ETag", "\"r1\"");
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
            response.Close();
        }

        private static int ReservePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
