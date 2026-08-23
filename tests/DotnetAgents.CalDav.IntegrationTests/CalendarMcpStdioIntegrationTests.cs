using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
            .ShouldBe($"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}");
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task CalendarEntityQuery_ReturnsSchemaValidSnapshotsAndTypedFailureOverStdio()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\n"
            + "BEGIN:VTODO\r\nUID:entity-query-stdio-1\r\nDTSTAMP:20260817T120000Z\r\n"
            + "SUMMARY:Entity query integration\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var href = await PutResourceAsync("entity-query-stdio-1.ics", content);
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(stderr, exposeExact: false);
        var tools = await client.ListToolsAsync(new ListToolsRequestParams(), TestContext.Current.CancellationToken);
        var advertised = tools.Tools.Single(tool => tool.Name == "calendar_entities.query");
        var selectedScope = new Dictionary<string, object?>
        {
            ["mode"] = "selected",
            ["calendar"] = new Dictionary<string, object?>
            {
                ["by"] = "href",
                ["href"] = $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}"
            }
        };

        var success = await client.CallToolAsync(
            "calendar_entities.query",
            new Dictionary<string, object?>
            {
                ["scope"] = selectedScope,
                ["entityKinds"] = new[] { "todo" },
                ["pageSize"] = 1
            },
            cancellationToken: TestContext.Current.CancellationToken);
        var failure = await client.CallToolAsync(
            "calendar_entities.query",
            new Dictionary<string, object?>
            {
                ["scope"] = new Dictionary<string, object?>
                {
                    ["mode"] = "selected",
                    ["calendar"] = new Dictionary<string, object?>
                    {
                        ["by"] = "name",
                        ["name"] = "No such authorized calendar"
                    }
                },
                ["entityKinds"] = new[] { "todo" }
            },
            cancellationToken: TestContext.Current.CancellationToken);

        success.IsError.ShouldBe(false);
        var structured = success.StructuredContent!.Value;
        structured.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["outcome", "items", "diagnostics", "pagination"]);
        structured.GetProperty("outcome").GetString().ShouldBe("success");
        structured.GetProperty("items").GetArrayLength().ShouldBe(1);
        structured.GetProperty("items")[0].GetProperty("resourceRevision").GetProperty("entityTag").GetString()
            .ShouldStartWith("\"");
        structured.GetProperty("pagination").GetProperty("mode").GetString().ShouldBe("query_result_snapshot");
        structured.GetProperty("diagnostics").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Array);
        advertised.OutputSchema.ShouldNotBeNull();
        advertised.OutputSchema.Value.GetProperty("oneOf").GetArrayLength().ShouldBe(2);
        advertised.InputSchema.GetProperty("oneOf").GetArrayLength().ShouldBe(2);
        failure.IsError.ShouldBe(true);
        var error = failure.StructuredContent!.Value;
        error.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["code", "category", "message", "retryable", "phase", "authorizedCandidates"]);
        error.GetProperty("code").GetString().ShouldBe("not_found");
        error.GetProperty("category").GetString().ShouldBe("selection");
        error.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
        error.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        error.GetProperty("phase").GetString().ShouldBe("selectionDiscoveryCapability");
        var candidate = error.GetProperty("authorizedCandidates").EnumerateArray().ShouldHaveSingleItem();
        candidate.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["calendar", "displayName", "entityKinds"]);
        candidate.GetProperty("calendar").GetProperty("href").GetString()
            .ShouldBe($"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}");
        candidate.GetProperty("displayName").GetString().ShouldBe("Tasks");
        candidate.GetProperty("entityKinds").EnumerateObject().Select(property => property.Name)
            .ShouldBe(["event", "todo"]);
        error.TryGetProperty("items", out _).ShouldBeFalse();
        stderr.ShouldBeEmpty();
        await DeleteResourceAsync(href);
    }

    [Fact]
    public async Task TodoQuery_ReturnsNormalizedCompactResultsBeforePaginationOverNativeStdioAndRadicale()
    {
        var calendarHref = _fixture.WorkCalendarHref;
        var hrefs = new List<string>();
        try
        {
            foreach (var index in Enumerable.Range(1, 40))
            {
                hrefs.Add(await PutResourceAsync(
                    calendarHref,
                    $"todo-query-completed-{index:00}.ics",
                    Todo($"todo-query-completed-{index:00}",
                        $"SUMMARY:Completed task {index:00}\r\nSTATUS:COMPLETED\r\nCOMPLETED:20260817T120000Z\r\nPERCENT-COMPLETE:100\r\nDESCRIPTION:{new string('x', 512)}\r\n")));
            }
            foreach (var index in Enumerable.Range(1, 4))
            {
                hrefs.Add(await PutResourceAsync(
                    calendarHref,
                    $"todo-query-open-{index:00}.ics",
                    Todo($"todo-query-open-{index:00}", $"SUMMARY:Open task {index:00}\r\n")));
            }
            hrefs.Add(await PutResourceAsync(calendarHref, "todo-query-cancelled.ics", Todo(
                "todo-query-cancelled", "SUMMARY:Cancelled task\r\nSTATUS:CANCELLED\r\n")));
            hrefs.Add(await PutResourceAsync(calendarHref, "todo-query-conflict.ics", Todo(
                "todo-query-conflict", "SUMMARY:Conflict task\r\nSTATUS:IN-PROCESS\r\nPERCENT-COMPLETE:100\r\n")));

            var stderr = new ConcurrentQueue<string>();
            await using var client = await CreateClientAsync(
                stderr,
                exposeExact: false,
                calendarHrefs: $"{_fixture.BaseUrl}{calendarHref}");
            var scope = new Dictionary<string, object?>
            {
                ["mode"] = "selected",
                ["calendar"] = new Dictionary<string, object?>
                {
                    ["by"] = "href",
                    ["href"] = $"{_fixture.BaseUrl}{calendarHref}"
                }
            };
            var openPages = new List<JsonElement>();
            Dictionary<string, object?>? openArguments = new() { ["scope"] = scope, ["pageSize"] = 2 };
            do
            {
                var open = await client.CallToolAsync(
                    "todos.query",
                    openArguments,
                    cancellationToken: TestContext.Current.CancellationToken);
                open.IsError.ShouldBe(false);
                var openStructured = open.StructuredContent!.Value;
                foreach (var item in openStructured.GetProperty("items").EnumerateArray())
                {
                    item.GetProperty("completionState").GetString().ShouldBe("open");
                    openPages.Add(item.Clone());
                }
                var nextCursor = openStructured.GetProperty("pagination").GetProperty("nextCursor");
                openArguments = nextCursor.ValueKind == JsonValueKind.Null
                    ? null
                    : new Dictionary<string, object?> { ["scope"] = scope, ["pageSize"] = 2, ["cursor"] = nextCursor.GetString() };
            }
            while (openArguments is not null);

            var explicitStates = await client.CallToolAsync(
                "todos.query",
                new Dictionary<string, object?>
                {
                    ["scope"] = scope,
                    ["completionStates"] = new[] { "completed", "cancelled", "indeterminate" },
                    ["pageSize"] = 200,
                    ["projection"] = new[] { "summary", "status", "completedAt", "percentComplete" }
                },
                cancellationToken: TestContext.Current.CancellationToken);

            openPages.Count.ShouldBe(4);
            openPages.Select(item => item.GetProperty("uid").GetString()).Distinct().Count().ShouldBe(4);
            openPages.Sum(item => Encoding.UTF8.GetByteCount(item.GetRawText())).ShouldBeLessThan(64 * 1024);
            explicitStates.IsError.ShouldBe(false);
            var states = explicitStates.StructuredContent!.Value.GetProperty("items")
                .EnumerateArray().Select(item => item.GetProperty("completionState").GetString()!).ToHashSet();
            states.OrderBy(value => value, StringComparer.Ordinal).ShouldBe(
                new[] { "completed", "cancelled", "indeterminate" }.OrderBy(value => value, StringComparer.Ordinal));
            explicitStates.StructuredContent.Value.GetProperty("excludedIndeterminateCount").GetInt32().ShouldBe(0);
            stderr.ShouldBeEmpty();
        }
        finally
        {
            foreach (var href in hrefs)
                await DeleteResourceAsync(href);
        }
    }

    [Fact]
    public async Task CalendarOccurrenceQuery_ExpandsAuthoritativeRecurringTodoOverRealStdioAndRadicale()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\n"
            + "BEGIN:VTODO\r\nUID:occurrence-stdio-1\r\nDTSTAMP:20260815T120000Z\r\n"
            + "DTSTART:20260815T100000Z\r\nDUE:20260815T103000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\n"
            + "END:VTODO\r\nEND:VCALENDAR\r\n";
        var href = await PutResourceAsync("occurrence-stdio-1.ics", content);
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(stderr, exposeExact: false);
        var tools = await client.ListToolsAsync(new ListToolsRequestParams(), TestContext.Current.CancellationToken);
        var advertised = tools.Tools.Single(tool => tool.Name == "calendar_occurrences.query");

        var result = await client.CallToolAsync(
            "calendar_occurrences.query",
            new Dictionary<string, object?>
            {
                ["scope"] = new Dictionary<string, object?>
                {
                    ["mode"] = "selected",
                    ["calendar"] = new Dictionary<string, object?>
                    {
                        ["by"] = "href",
                        ["href"] = $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}"
                    }
                },
                ["from"] = new Dictionary<string, object?>
                {
                    ["kind"] = "utcDateTime",
                    ["value"] = "2026-08-16T10:15:00Z"
                },
                ["to"] = new Dictionary<string, object?>
                {
                    ["kind"] = "utcDateTime",
                    ["value"] = "2026-08-16T10:20:00Z"
                }
            },
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(false);
        var occurrence = result.StructuredContent!.Value.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        occurrence.GetProperty("snapshot").GetProperty("resourceRevision").GetProperty("href").GetString().ShouldBe(href);
        occurrence.GetProperty("snapshot").GetProperty("projection").GetProperty("kind").GetString().ShouldBe("todo");
        occurrence.GetProperty("recurrenceIdentity").GetProperty("value").GetProperty("value").GetString()
            .ShouldBe("2026-08-16T10:00:00Z");
        occurrence.GetProperty("timing").GetProperty("effectiveEnd").GetProperty("value").GetString()
            .ShouldBe("2026-08-16T10:30:00Z");
        advertised.InputSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(["scope", "from", "to"]);
        advertised.Meta!["cache"]!["ttlMs"]!.GetValue<int>().ShouldBe(5000);
        advertised.Meta!["cache"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task CalendarOccurrenceQuery_ReturnsFiveSelectedCalendarResourcesBeforeReadDeadline()
    {
        var hrefs = new List<string>();
        try
        {
            foreach (var index in Enumerable.Range(1, 5))
            {
                hrefs.Add(await PutResourceAsync(
                    $"occurrence-five-{index}.ics",
                    Todo($"occurrence-five-{index}", "DUE:20260823T100000Z\r\n")));
            }

            var stderr = new ConcurrentQueue<string>();
            await using var client = await CreateClientAsync(stderr, exposeExact: false);

            var stopwatch = Stopwatch.StartNew();
            var result = await CallOccurrenceAsync(client, "2026-08-23T09:59:00Z", "2026-08-23T10:01:00Z");
            stopwatch.Stop();

            result.IsError.ShouldBe(false);
            result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("success");
            result.StructuredContent.Value.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("snapshot").GetProperty("projection").GetProperty("uid").GetString())
                .ShouldBe(Enumerable.Range(1, 5).Select(index => $"occurrence-five-{index}").ToArray(), ignoreOrder: true);
            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            stderr.ShouldBeEmpty();
        }
        finally
        {
            foreach (var href in hrefs)
                await DeleteResourceAsync(href);
        }
    }

    [Fact]
    public async Task CalendarOccurrenceQuery_ProvesBoundaryDstLeapRangeAndTypedFailuresOverRealStdioAndRadicale()
    {
        var boundaryHref = await PutResourceAsync("occurrence-boundary.ics", Todo(
            "occurrence-boundary", "DUE:20260816T100000Z\r\n"));
        _ = await PutResourceAsync("occurrence-boundary-to.ics", Todo(
            "occurrence-boundary-to", "DUE:20260816T110000Z\r\n"));
        _ = await PutResourceAsync("occurrence-dst.ics", Todo(
            "occurrence-dst", "DTSTART;TZID=America/New_York:20260307T100000\r\n"
            + "DURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=3\r\n"));
        _ = await PutResourceAsync("occurrence-leap.ics", Todo(
            "occurrence-leap", "DTSTART:20240229T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=YEARLY;COUNT=3\r\n"));
        var rangeHref = await PutResourceAsync("occurrence-range.ics", RangeTodo());
        var rangeObserved = await GetResourceAsync(rangeHref);
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(stderr, exposeExact: false);

        var boundary = await CallOccurrenceAsync(client, "2026-08-16T10:00:00Z", "2026-08-16T11:00:00Z");
        var dst = await CallOccurrenceAsync(client, "2026-03-08T14:30:00Z", "2026-03-08T14:45:00Z");
        var leap = await CallOccurrenceAsync(client, "2028-02-29T10:30:00Z", "2028-02-29T10:45:00Z");
        var moved = await CallOccurrenceAsync(client, "2026-08-17T13:30:00Z", "2026-08-17T13:45:00Z");

        boundary.IsError.ShouldBe(false);
        var boundaryItems = boundary.StructuredContent!.Value.GetProperty("items").EnumerateArray().ToArray();
        boundaryItems.ShouldContain(item => item.GetProperty("snapshot").GetProperty("resourceRevision")
            .GetProperty("href").GetString() == boundaryHref);
        boundaryItems.ShouldNotContain(item => item.GetProperty("snapshot").GetProperty("projection")
            .GetProperty("uid").GetString() == "occurrence-boundary-to");
        dst.StructuredContent!.Value.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("snapshot").GetProperty("projection").GetProperty("uid").GetString() == "occurrence-dst")
            .GetProperty("timing").GetProperty("evaluatedStartUtc").GetProperty("value").GetString()
            .ShouldBe("2026-03-08T14:00:00Z");
        leap.StructuredContent!.Value.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("snapshot").GetProperty("projection").GetProperty("uid").GetString() == "occurrence-leap")
            .GetProperty("recurrenceIdentity").GetProperty("value").GetProperty("value").GetString()
            .ShouldBe("2028-02-29T10:00:00Z");
        var movedOccurrence = moved.StructuredContent!.Value.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("snapshot").GetProperty("projection").GetProperty("uid").GetString() == "occurrence-range");
        movedOccurrence.GetProperty("recurrenceIdentity").GetProperty("value").GetProperty("value").GetString()
            .ShouldBe("2026-08-17T09:00:00Z");
        movedOccurrence.GetProperty("timing").GetProperty("effectiveStart").GetProperty("value").GetString()
            .ShouldBe("2026-08-17T13:00:00Z");
        movedOccurrence.GetProperty("snapshot").TryGetProperty("authoritativePayload", out _).ShouldBeFalse();
        movedOccurrence.ToString().ShouldNotContain(Convert.ToBase64String(rangeObserved.Utf8));

        var unresolvedHref = await PutResourceAsync("occurrence-unresolved.ics", Todo(
            "occurrence-unresolved", "DTSTART;TZID=Private/Unknown:20260816T100000\r\nDURATION:PT1H\r\n"));
        var unresolved = await CallOccurrenceAsync(client, "2026-08-16T00:00:00Z", "2026-08-17T00:00:00Z");
        unresolved.IsError.ShouldBe(true);
        unresolved.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("temporal_unresolved");
        await DeleteResourceAsync(unresolvedHref);

        var unevaluableHref = await PutResourceAsync("occurrence-unevaluable.ics", Todo(
            "occurrence-unevaluable", "DTSTART:20260816T100000Z\r\nDURATION:PT1H\r\n"
            + "RRULE:FREQ=DAILY;COUNT=2\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\n"));
        var unevaluable = await CallOccurrenceAsync(client, "2026-08-16T00:00:00Z", "2026-08-17T00:00:00Z");
        unevaluable.IsError.ShouldBe(true);
        unevaluable.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("recurrence_unevaluable");
        await DeleteResourceAsync(unevaluableHref);
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateTools_UseAuthoritativeConditionalPutAgainstPopulatedMixedCalendar()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mixedCalendarHref = $"{_fixture.BaseUrl}{_fixture.MixedCalendarHref}";
        var seededHrefs = new List<string>(65);
        for (var index = 0; index < 48; index++)
        {
            seededHrefs.Add(await PutResourceAsync(
                _fixture.MixedCalendarHref,
                $"seed-todo-{suffix}-{index}.ics",
                Todo($"seed-todo-{suffix}-{index}", $"SUMMARY:Seed To-do {index}\r\n")));
        }
        for (var index = 0; index < 16; index++)
        {
            seededHrefs.Add(await PutResourceAsync(
                _fixture.MixedCalendarHref,
                $"seed-event-{suffix}-{index}.ics",
                Event(
                    $"seed-event-{suffix}-{index}",
                    $"SUMMARY:Seed Event {index}\r\nDTSTART:20260818T{index:00}0000Z\r\n")));
        }
        var duplicateUid = $"seed-cross-kind-{suffix}";
        seededHrefs.Add(await PutResourceAsync(
            _fixture.MixedCalendarHref,
            $"imported-cross-kind-{suffix}.ics",
            Todo(duplicateUid, "SUMMARY:Imported cross-kind identity\r\n")));

        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(
            stderr,
            exposeExact: true,
            calendarHrefs: mixedCalendarHref,
            confirmMutations: true);
        var destination = new Dictionary<string, object?>
        {
            ["mode"] = "selected",
            ["calendar"] = new Dictionary<string, object?>
            {
                ["by"] = "href",
                ["href"] = mixedCalendarHref
            }
        };
        var eventUid = $"authoritative-event-{suffix}";
        var todoUid = $"authoritative-todo-{suffix}";
        var eventCreate = await client.CallToolAsync(
            "events.create",
            new Dictionary<string, object?>
            {
                ["destination"] = destination,
                ["entity"] = new Dictionary<string, object?>
                {
                    ["kind"] = "event",
                    ["uid"] = eventUid,
                    ["fields"] = new Dictionary<string, object?>
                    {
                        ["summary"] = "Authoritative event",
                        ["start"] = new Dictionary<string, object?>
                        {
                            ["kind"] = "utcDateTime",
                            ["value"] = "2026-08-18T13:00:00Z"
                        }
                    }
                }
            },
            cancellationToken: TestContext.Current.CancellationToken);
        var todoCreate = await client.CallToolAsync(
            "todos.create",
            new Dictionary<string, object?>
            {
                ["destination"] = destination,
                ["entity"] = new Dictionary<string, object?>
                {
                    ["kind"] = "todo",
                    ["uid"] = todoUid,
                    ["fields"] = new Dictionary<string, object?> { ["summary"] = "Authoritative To-do" }
                }
            },
            cancellationToken: TestContext.Current.CancellationToken);
        var uidCollision = await client.CallToolAsync(
            "events.create",
            new Dictionary<string, object?>
            {
                ["destination"] = destination,
                ["entity"] = new Dictionary<string, object?>
                {
                    ["kind"] = "event",
                    ["uid"] = duplicateUid,
                    ["fields"] = new Dictionary<string, object?>
                    {
                        ["start"] = new Dictionary<string, object?>
                        {
                            ["kind"] = "utcDateTime",
                            ["value"] = "2026-08-18T14:00:00Z"
                        }
                    }
                }
            },
            cancellationToken: TestContext.Current.CancellationToken);
        var exactHref = $"{mixedCalendarHref}authoritative-exact-{suffix}.ics";
        var exactCreate = await client.CallToolAsync(
            "calendar_resources.exact_create",
            new Dictionary<string, object?>
            {
                ["destinationHref"] = exactHref,
                ["utf8Resource"] = ExactEvent(
                    $"authoritative-exact-{suffix}",
                    "Authoritative exact",
                    "X-TEST:conditional-put")
            },
            cancellationToken: TestContext.Current.CancellationToken);
        var destinationCollision = await client.CallToolAsync(
            "calendar_resources.exact_create",
            new Dictionary<string, object?>
            {
                ["destinationHref"] = seededHrefs[0],
                ["utf8Resource"] = ExactEvent(
                    $"other-exact-{suffix}",
                    "Must not overwrite",
                    "X-TEST:destination-conflict")
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var eventRevision = AssertCommittedCreate(eventCreate, "event", eventUid, mixedCalendarHref);
        var todoRevision = AssertCommittedCreate(todoCreate, "todo", todoUid, mixedCalendarHref);
        exactCreate.IsError.ShouldBe(false);
        uidCollision.IsError.ShouldBe(true);
        uidCollision.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("conflict");
        uidCollision.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_committed");
        destinationCollision.IsError.ShouldBe(true);
        destinationCollision.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("destination_conflict");
        destinationCollision.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        Encoding.UTF8.GetString((await GetResourceAsync(seededHrefs[0])).Utf8).ShouldContain("Seed To-do 0");
        stderr.ShouldBeEmpty();

        await DeleteResourceAsync(eventRevision.Href);
        await DeleteResourceAsync(todoRevision.Href);
        await DeleteResourceAsync(exactHref);
        foreach (var seededHref in seededHrefs)
            await DeleteResourceAsync(seededHref);
    }

    [Fact]
    public async Task CalendarEntityCreate_CreatesEventAndTodoAndRejectsCallerUidCollisionOverRealStdioAndRadicale()
    {
        var stderr = new ConcurrentQueue<string>();
        var eventCalendarHref = $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}";
        var todoCalendarHref = $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}";
        await using var client = await CreateClientAsync(
            stderr,
            exposeExact: false,
            calendarHrefs: $"{eventCalendarHref},{todoCalendarHref}");
        var tools = await client.ListToolsAsync(new ListToolsRequestParams(), TestContext.Current.CancellationToken);
        var eventSchema = tools.Tools.Single(tool => tool.Name == "events.create").InputSchema;
        var todoSchema = tools.Tools.Single(tool => tool.Name == "todos.create").InputSchema;
        eventSchema.GetProperty("$defs").GetProperty("eventInputFields").GetProperty("properties")
            .GetProperty("recurrenceSet").GetProperty("$ref").GetString()
            .ShouldBe("#/$defs/eventRecurrenceSetInput");
        todoSchema.GetProperty("$defs").GetProperty("todoInputFields").GetProperty("properties")
            .GetProperty("recurrenceSet").GetProperty("$ref").GetString()
            .ShouldBe("#/$defs/todoRecurrenceSetInput");
        var eventOverrideSchema = eventSchema.GetProperty("$defs").GetProperty("eventRecurrenceOverrideInput");
        eventOverrideSchema.GetProperty("properties").TryGetProperty("entityKind", out _).ShouldBeFalse();
        eventOverrideSchema.GetProperty("properties").TryGetProperty("uid", out _).ShouldBeFalse();
        eventOverrideSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .ShouldBe(["recurrenceIdentity", "status", "fields"]);
        eventSchema.GetProperty("$defs").GetProperty("recurrenceDateInput").GetProperty("oneOf")
            .GetArrayLength().ShouldBe(2);
        var eventDestination = new Dictionary<string, object?>
        {
            ["mode"] = "selected",
            ["calendar"] = new Dictionary<string, object?>
            {
                ["by"] = "href",
                ["href"] = eventCalendarHref
            }
        };
        var todoDestination = new Dictionary<string, object?>
        {
            ["mode"] = "selected",
            ["calendar"] = new Dictionary<string, object?>
            {
                ["by"] = "href",
                ["href"] = todoCalendarHref
            }
        };
        var eventArguments = new Dictionary<string, object?>
        {
            ["destination"] = eventDestination,
            ["entity"] = new Dictionary<string, object?>
            {
                ["kind"] = "event",
                ["uid"] = "stdio-create-event",
                ["fields"] = new Dictionary<string, object?>
                {
                    ["summary"] = "Stdio create event",
                    ["start"] = new Dictionary<string, object?>
                    {
                        ["kind"] = "utcDateTime",
                        ["value"] = "2026-08-18T13:00:00Z"
                    },
                    ["duration"] = "PT1H",
                    ["recurrenceSet"] = new Dictionary<string, object?>
                    {
                        ["rrule"] = "FREQ=DAILY;COUNT=2",
                        ["rdates"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["kind"] = "utcDateTime",
                                ["value"] = "2026-08-20T13:00:00Z"
                            }
                        },
                        ["exdates"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["kind"] = "utcDateTime",
                                ["value"] = "2026-08-19T13:00:00Z"
                            }
                        },
                        ["overrides"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["recurrenceIdentity"] = new Dictionary<string, object?>
                                {
                                    ["value"] = new Dictionary<string, object?>
                                    {
                                        ["kind"] = "utcDateTime",
                                        ["value"] = "2026-08-20T13:00:00Z"
                                    }
                                },
                                ["status"] = "active",
                                ["fields"] = new Dictionary<string, object?>
                                {
                                    ["summary"] = "Moved stdio event",
                                    ["start"] = new Dictionary<string, object?>
                                    {
                                        ["kind"] = "utcDateTime",
                                        ["value"] = "2026-08-20T15:00:00Z"
                                    },
                                    ["duration"] = "PT1H"
                                }
                            }
                        }
                    },
                    ["structuredData"] = new Dictionary<string, object?>
                    {
                        ["attachments"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["uri"] = "https://storage.example.test/stdio-event",
                                ["label"] = "Agenda",
                                ["parameters"] = Array.Empty<object>()
                            }
                        }
                    }
                }
            }
        };
        var todoArguments = new Dictionary<string, object?>
        {
            ["destination"] = todoDestination,
            ["entity"] = new Dictionary<string, object?>
            {
                ["kind"] = "todo",
                ["uid"] = "stdio-create-todo",
                ["fields"] = new Dictionary<string, object?>
                {
                    ["summary"] = "Stdio create todo",
                    ["start"] = new Dictionary<string, object?>
                    {
                        ["kind"] = "date",
                        ["value"] = "2026-08-18"
                    },
                    ["due"] = new Dictionary<string, object?>
                    {
                        ["kind"] = "date",
                        ["value"] = "2026-08-19"
                    },
                    ["recurrenceSet"] = new Dictionary<string, object?>
                    {
                        ["rdates"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["kind"] = "date",
                                ["value"] = "2026-08-25"
                            }
                        },
                        ["exdates"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["kind"] = "date",
                                ["value"] = "2026-09-01"
                            }
                        },
                        ["overrides"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["recurrenceIdentity"] = new Dictionary<string, object?>
                                {
                                    ["value"] = new Dictionary<string, object?>
                                    {
                                        ["kind"] = "date",
                                        ["value"] = "2026-08-25"
                                    }
                                },
                                ["status"] = "cancelled",
                                ["fields"] = new Dictionary<string, object?>
                                {
                                    ["summary"] = "Cancelled stdio todo",
                                    ["start"] = new Dictionary<string, object?>
                                    {
                                        ["kind"] = "date",
                                        ["value"] = "2026-08-25"
                                    },
                                    ["due"] = new Dictionary<string, object?>
                                    {
                                        ["kind"] = "date",
                                        ["value"] = "2026-08-26"
                                    }
                                }
                            }
                        }
                    },
                    ["structuredData"] = new Dictionary<string, object?>
                    {
                        ["comments"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["value"] = "Stored through stdio",
                                ["parameters"] = Array.Empty<object>()
                            }
                        }
                    }
                }
            }
        };

        var createdEvent = await client.CallToolAsync(
            "events.create",
            eventArguments,
            cancellationToken: TestContext.Current.CancellationToken);
        var createdTodo = await client.CallToolAsync(
            "todos.create",
            todoArguments,
            cancellationToken: TestContext.Current.CancellationToken);
        var collisionEntity = (Dictionary<string, object?>)eventArguments["entity"]!;
        var collisionFields = (Dictionary<string, object?>)collisionEntity["fields"]!;
        collisionFields["summary"] = "Must not overwrite";
        var collision = await client.CallToolAsync(
            "events.create",
            eventArguments,
            cancellationToken: TestContext.Current.CancellationToken);

        var eventRevision = AssertCommittedCreate(createdEvent, "event", "stdio-create-event", eventCalendarHref);
        var todoRevision = AssertCommittedCreate(createdTodo, "todo", "stdio-create-todo", todoCalendarHref);
        collision.IsError.ShouldBe(true);
        collision.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("destination_conflict");
        collision.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_committed");
        var observedEvent = await GetResourceAsync(eventRevision.Href);
        var observedTodo = await GetResourceAsync(todoRevision.Href);
        var storedEvent = Encoding.UTF8.GetString(observedEvent.Utf8);
        var storedTodo = Encoding.UTF8.GetString(observedTodo.Utf8);
        observedEvent.EntityTag.ShouldBe(eventRevision.EntityTag);
        observedTodo.EntityTag.ShouldBe(todoRevision.EntityTag);
        storedEvent.ShouldContain("SUMMARY:Stdio create event");
        storedEvent.ShouldNotContain("Must not overwrite");
        storedEvent.ShouldContain("ATTACH;LABEL=Agenda:https://storage.example.test/stdio-event");
        storedEvent.ShouldContain("RRULE:FREQ=DAILY;COUNT=2");
        storedEvent.ShouldContain("RDATE:20260820T130000Z");
        storedEvent.ShouldContain("EXDATE:20260819T130000Z");
        storedEvent.ShouldContain("RECURRENCE-ID:20260820T130000Z");
        storedEvent.ShouldContain("SUMMARY:Moved stdio event");
        storedEvent.Split("UID:stdio-create-event", StringSplitOptions.None).Length.ShouldBe(3);
        storedTodo.ShouldContain("SUMMARY:Stdio create todo");
        storedTodo.ShouldContain("COMMENT:Stored through stdio");
        storedTodo.ShouldNotContain("RRULE:");
        storedTodo.ShouldContain("RDATE;VALUE=DATE:20260825");
        storedTodo.ShouldContain("EXDATE;VALUE=DATE:20260901");
        storedTodo.ShouldContain("RECURRENCE-ID;VALUE=DATE:20260825");
        storedTodo.ShouldContain("STATUS:CANCELLED");
        storedTodo.Split("UID:stdio-create-todo", StringSplitOptions.None).Length.ShouldBe(3);
        stderr.ShouldBeEmpty();

        await DeleteResourceAsync(eventRevision.Href, observedEvent.EntityTag);
        await DeleteResourceAsync(todoRevision.Href, observedTodo.EntityTag);
    }

    [Fact]
    public async Task CalendarEntityPatch_PatchesOneEventOverRealStdioAndRadicale()
    {
        const string uid = "stdio-patch-event";
        var href = await PutResourceAsync(
            _fixture.EventCalendarHref,
            "stdio-patch-event.ics",
            Event(uid, "SUMMARY:Before\r\nDTSTART:20260818T130000Z\r\nDURATION:PT1H\r\n"));
        var before = await GetResourceAsync(href);
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(
            stderr,
            exposeExact: false,
            calendarHrefs: $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}");

        var patched = await client.CallToolAsync(
            "events.patch",
            PatchArguments(new ObservedRevision(href, before.EntityTag), "event", uid, "After"),
            cancellationToken: TestContext.Current.CancellationToken);

        var revision = AssertCommittedCreate(
            patched,
            "event",
            uid,
            $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}");
        var observed = await GetResourceAsync(href);
        observed.EntityTag.ShouldBe(revision.EntityTag);
        var stored = Encoding.UTF8.GetString(observed.Utf8);
        stored.ShouldContain("SUMMARY:After");
        stored.ShouldContain("CATEGORIES:Patched");
        stderr.ShouldBeEmpty();
        await DeleteResourceAsync(href, observed.EntityTag);
    }

    [Fact]
    public async Task CalendarEntityPatch_PatchesMovedOccurrenceOverNativeStdioAndRadicale()
    {
        const string uid = "stdio-patch-occurrence";
        var href = await PutResourceAsync(
            _fixture.EventCalendarHref,
            "stdio-patch-occurrence.ics",
            Event(uid, "SUMMARY:Master\r\nDESCRIPTION:Inherited\r\nDTSTART:20260818T130000Z\r\nDTEND:20260818T140000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\n"));
        var before = await GetResourceAsync(href);
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(
            stderr,
            exposeExact: false,
            calendarHrefs: $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}");

        var patched = await client.CallToolAsync(
            "events.patch",
            OneOccurrencePatchArguments(new ObservedRevision(href, before.EntityTag), uid),
            cancellationToken: TestContext.Current.CancellationToken);

        var revision = AssertCommittedCreate(
            patched,
            "event",
            uid,
            $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}");
        var observed = await GetResourceAsync(href);
        observed.EntityTag.ShouldBe(revision.EntityTag);
        var stored = Encoding.UTF8.GetString(observed.Utf8);
        stored.ShouldContain("RECURRENCE-ID:20260819T130000Z");
        stored.ShouldContain("DTSTART:20260819T160000Z");
        stored.ShouldContain("DTEND:20260819T170000Z");
        stored.ShouldContain("SUMMARY:Moved once");
        stored.ShouldContain("DESCRIPTION:Inherited");
        stored.Split("UID:stdio-patch-occurrence", StringSplitOptions.None).Length.ShouldBe(3);
        stderr.ShouldBeEmpty();
        await DeleteResourceAsync(href, observed.EntityTag);
    }

    [Fact]
    public async Task CalendarEntityPatch_PatchesOneReviewedTodoOverRealStdioAndRadicale()
    {
        const string uid = "stdio-patch-todo";
        var href = await PutResourceAsync("stdio-patch-todo.ics", Todo(uid, "SUMMARY:Before\r\n"));
        var before = await GetResourceAsync(href);
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(stderr, exposeExact: false);

        var patched = await client.CallToolAsync(
            "todos.patch",
            PatchArguments(new ObservedRevision(href, before.EntityTag), "todo", uid, "After"),
            cancellationToken: TestContext.Current.CancellationToken);

        var revision = AssertCommittedCreate(
            patched,
            "todo",
            uid,
            $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}");
        var observed = await GetResourceAsync(href);
        observed.EntityTag.ShouldBe(revision.EntityTag);
        var stored = Encoding.UTF8.GetString(observed.Utf8);
        stored.ShouldContain("SUMMARY:After");
        stored.ShouldContain("CATEGORIES:Patched");
        stderr.ShouldBeEmpty();
        await DeleteResourceAsync(href, revision.EntityTag);
    }

    [Fact]
    public async Task CalendarEntityPatch_AppliesReviewedRangeEventAndEntireTodoOverNativeStdioAndRadicale()
    {
        const string eventUid = "stdio-patch-range-event";
        const string eventContent = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\nBEGIN:VEVENT\r\nUID:stdio-patch-range-event\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260818T130000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nSUMMARY:Master\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:stdio-patch-range-event\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260820T130000Z\r\nDTSTART:20260820T150000Z\r\nSUMMARY:Individual\r\nX-KEEP:event-offset\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var eventHref = await PutResourceAsync(
            _fixture.EventCalendarHref,
            "stdio-patch-range-event.ics",
            eventContent);
        var eventBefore = await GetResourceAsync(eventHref);
        var eventStderr = new ConcurrentQueue<string>();
        await using var eventClient = await CreateClientAsync(
            eventStderr,
            exposeExact: false,
            calendarHrefs: $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}",
            confirmMutations: true);

        var eventResult = await eventClient.CallToolAsync(
            "events.patch",
            BroadPatchArguments(
                new ObservedRevision(eventHref, eventBefore.EntityTag),
                "event",
                eventUid,
                "this-and-future",
                "Reviewed future",
                "2026-08-19T13:00:00Z"),
            cancellationToken: TestContext.Current.CancellationToken);

        var eventRevision = AssertCommittedCreate(
            eventResult,
            "event",
            eventUid,
            $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}");
        var storedEvent = Encoding.UTF8.GetString((await GetResourceAsync(eventHref)).Utf8);
        storedEvent.ShouldContain("RECURRENCE-ID;RANGE=THISANDFUTURE:20260819T130000Z");
        storedEvent.Split("SUMMARY:Reviewed future", StringSplitOptions.None).Length.ShouldBe(3);
        storedEvent.ShouldContain("X-KEEP:event-offset");
        eventStderr.ShouldBeEmpty();

        const string todoUid = "stdio-patch-entire-todo";
        const string todoContent = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\nBEGIN:VTODO\r\nUID:stdio-patch-entire-todo\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260818T090000Z\r\nDUE:20260818T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nSUMMARY:Master\r\nX-MASTER:keep\r\nEND:VTODO\r\nBEGIN:VTODO\r\nUID:stdio-patch-entire-todo\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20260819T090000Z\r\nDTSTART:20260819T110000Z\r\nDUE:20260819T120000Z\r\nSUMMARY:Override\r\nX-OVERRIDE:keep\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var todoHref = await PutResourceAsync("stdio-patch-entire-todo.ics", todoContent);
        var todoBefore = await GetResourceAsync(todoHref);
        var todoStderr = new ConcurrentQueue<string>();
        await using var todoClient = await CreateClientAsync(todoStderr, exposeExact: false, confirmMutations: true);

        var todoResult = await todoClient.CallToolAsync(
            "todos.patch",
            BroadPatchArguments(
                new ObservedRevision(todoHref, todoBefore.EntityTag),
                "todo",
                todoUid,
                "entire-set",
                "Reviewed all"),
            cancellationToken: TestContext.Current.CancellationToken);

        var todoRevision = AssertCommittedCreate(
            todoResult,
            "todo",
            todoUid,
            $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}");
        var storedTodo = Encoding.UTF8.GetString((await GetResourceAsync(todoHref)).Utf8);
        storedTodo.Split("SUMMARY:Reviewed all", StringSplitOptions.None).Length.ShouldBe(3);
        storedTodo.ShouldContain("X-MASTER:keep");
        storedTodo.ShouldContain("X-OVERRIDE:keep");
        todoStderr.ShouldBeEmpty();

        await DeleteResourceAsync(eventHref, eventRevision.EntityTag);
        await DeleteResourceAsync(todoHref, todoRevision.EntityTag);
    }

    [Fact]
    public async Task CalendarOccurrenceMembership_RoundTripsAllDirectMutationsOverNativeStdioAndRadicale()
    {
        const string uid = "stdio-occurrence-membership";
        var href = await PutResourceAsync("stdio-occurrence-membership.ics", Todo(
            uid,
            "SUMMARY:Membership series\r\nDTSTART:20260818T090000Z\r\n"
            + "DUE:20260818T100000Z\r\nRRULE:FREQ=DAILY;COUNT=2\r\nX-KEEP:opaque\r\n"));
        var observed = await GetResourceAsync(href);
        var revision = new ObservedRevision(href, observed.EntityTag);
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(stderr, exposeExact: false);

        revision = AssertCommittedCreate(
            await CallOccurrenceMutationAsync(client, "add", revision, uid, "2026-08-20T09:00:00Z"),
            "todo",
            uid,
            $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}");
        revision = AssertCommittedCreate(
            await CallOccurrenceMutationAsync(client, "exclude", revision, uid, "2026-08-19T09:00:00Z"),
            "todo",
            uid,
            $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}");
        revision = AssertCommittedCreate(
            await CallOccurrenceMutationAsync(client, "cancel", revision, uid, "2026-08-19T09:00:00Z"),
            "todo",
            uid,
            $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}");
        revision = AssertCommittedCreate(
            await CallOccurrenceMutationAsync(
                client,
                "restore_cancellation",
                revision,
                uid,
                "2026-08-19T09:00:00Z"),
            "todo",
            uid,
            $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}");
        revision = AssertCommittedCreate(
            await CallOccurrenceMutationAsync(
                client,
                "restore_exclusion",
                revision,
                uid,
                "2026-08-19T09:00:00Z"),
            "todo",
            uid,
            $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}");

        observed = await GetResourceAsync(href);
        observed.EntityTag.ShouldBe(revision.EntityTag);
        var stored = Encoding.UTF8.GetString(observed.Utf8);
        stored.ShouldContain("RDATE:20260820T090000Z");
        stored.ShouldContain("RECURRENCE-ID:20260819T090000Z");
        stored.ShouldContain("SUMMARY:Membership series");
        stored.ShouldContain("X-KEEP:opaque");
        stored.ShouldNotContain("EXDATE:20260819T090000Z");
        stored.ShouldNotContain("STATUS:CANCELLED");
        stored.Split($"UID:{uid}", StringSplitOptions.None).Length.ShouldBe(3);
        stderr.ShouldBeEmpty();
        await DeleteResourceAsync(href, revision.EntityTag);
    }

    [Fact]
    public async Task TodoCompletion_CompletesOneRecurringOccurrenceOverNativeStdioAndRadicale()
    {
        const string uid = "stdio-todo-completion";
        var href = await PutResourceAsync("stdio-todo-completion.ics", Todo(
            uid,
            "SUMMARY:Completion series\r\nDTSTART:20260818T090000Z\r\n"
            + "DUE:20260818T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nX-KEEP:opaque\r\n"));
        var observed = await GetResourceAsync(href);
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(stderr, exposeExact: false);
        var earliestCompletion = DateTimeOffset.UtcNow.AddSeconds(-1);

        var result = await client.CallToolAsync(
            "todos.complete",
            TodoCompletionArguments(new ObservedRevision(href, observed.EntityTag), uid),
            cancellationToken: TestContext.Current.CancellationToken);

        var revision = AssertCommittedCreate(
            result,
            "todo",
            uid,
            $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}");
        observed = await GetResourceAsync(href);
        observed.EntityTag.ShouldBe(revision.EntityTag);
        var stored = Encoding.UTF8.GetString(observed.Utf8);
        stored.ShouldContain("RECURRENCE-ID:20260819T090000Z");
        stored.ShouldContain("DTSTART:20260819T090000Z");
        stored.ShouldContain("DUE:20260819T100000Z");
        stored.ShouldContain("SUMMARY:Completion series");
        stored.ShouldContain("X-KEEP:opaque");
        stored.Split("STATUS:COMPLETED", StringSplitOptions.None).Length.ShouldBe(2);
        stored.Split("PERCENT-COMPLETE:100", StringSplitOptions.None).Length.ShouldBe(2);
        stored.Split("BEGIN:VTODO", StringSplitOptions.None).Length.ShouldBe(3);
        stored.ShouldNotContain("RECURRENCE-ID:20260820T090000Z");
        var completed = stored.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("COMPLETED:", StringComparison.Ordinal))["COMPLETED:".Length..];
        var completionInstant = DateTimeOffset.ParseExact(
            completed,
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        completionInstant.ShouldBeGreaterThanOrEqualTo(earliestCompletion);
        completionInstant.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(1));
        stderr.ShouldBeEmpty();
        await DeleteResourceAsync(href, revision.EntityTag);
    }

    [Fact]
    public async Task CalendarResourceMove_AtomicallyMovesReviewedTodoAcrossRadicaleCalendars()
    {
        const string uid = "stdio-move-1";
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\n"
            + "BEGIN:VTODO\r\nUID:stdio-move-1\r\nDTSTAMP:20260815T120000Z\r\n"
            + "SUMMARY:Reviewed move\r\nX-KEEP;P=One,one:opaque\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var sourceHref = await PutResourceAsync("stdio-move-1.ics", content);
        var source = await GetResourceAsync(sourceHref);
        var stderr = new ConcurrentQueue<string>();
        var authorizedCalendars = string.Join(',',
            $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}",
            $"{_fixture.BaseUrl}{_fixture.ShoppingCalendarHref}");
        await using var client = await CreateClientAsync(
            stderr,
            exposeExact: false,
            calendarHrefs: authorizedCalendars);

        var result = await client.CallToolAsync(
            "calendar_resources.move",
            new Dictionary<string, object?>
            {
                ["revision"] = new Dictionary<string, object?>
                {
                    ["href"] = sourceHref,
                    ["entityUid"] = uid,
                    ["entityKind"] = "todo",
                    ["entityTag"] = source.EntityTag
                },
                ["destination"] = new Dictionary<string, object?>
                {
                    ["mode"] = "selected",
                    ["calendar"] = new Dictionary<string, object?>
                    {
                        ["by"] = "href",
                        ["href"] = $"{_fixture.BaseUrl}{_fixture.ShoppingCalendarHref}"
                    }
                }
            },
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("outcome").GetString().ShouldBe("success");
        structured.GetProperty("mutationState").GetString().ShouldBe("committed");
        var revision = structured.GetProperty("snapshot").GetProperty("resourceRevision");
        var destinationHref = revision.GetProperty("href").GetString().ShouldNotBeNull();
        destinationHref.ShouldStartWith($"{_fixture.BaseUrl}{_fixture.ShoppingCalendarHref}");
        var projection = structured.GetProperty("snapshot").GetProperty("projection");
        projection.GetProperty("uid").GetString().ShouldBe(uid);
        projection.GetProperty("kind").GetString().ShouldBe("todo");
        (await GetStatusAsync(sourceHref)).ShouldBe(HttpStatusCode.NotFound);
        (await GetResourceAsync(destinationHref)).Utf8.ShouldBe(source.Utf8);
        stderr.ShouldBeEmpty();
        await DeleteResourceAsync(destinationHref);
    }

    [Fact]
    public async Task CalendarResourceDelete_ConfirmedMrtrDeletesOneReviewedResourceOverStdio()
    {
        const string content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\nBEGIN:VTODO\r\nUID:stdio-delete-1\r\nDTSTAMP:20260815T120000Z\r\nSUMMARY:Reviewed delete\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        var href = await PutResourceAsync("stdio-delete-1.ics", content);
        var observed = await GetResourceAsync(href);
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(stderr, exposeExact: false, confirmMutations: true);

        var result = await client.CallToolAsync(
            "calendar_resources.delete",
            new Dictionary<string, object?>
            {
                ["revision"] = new Dictionary<string, object?>
                {
                    ["href"] = href,
                    ["entityUid"] = "stdio-delete-1",
                    ["entityKind"] = "todo",
                    ["entityTag"] = observed.EntityTag
                }
            },
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("outcome").GetString().ShouldBe("success");
        structured.GetProperty("mutationState").GetString().ShouldBe("committed");
        var receipt = structured.GetProperty("deletionReceipt");
        receipt.GetProperty("href").GetString().ShouldBe(href);
        receipt.GetProperty("entityUid").GetString().ShouldBe("stdio-delete-1");
        receipt.GetProperty("entityKind").GetString().ShouldBe("todo");
        receipt.GetProperty("consumedEntityTag").GetString().ShouldBe(observed.EntityTag);
        (await GetStatusAsync(href)).ShouldBe(HttpStatusCode.NotFound);
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task CalendarEntityQuery_InvalidRawShapesReturnTypedErrorsWithoutNetwork()
    {
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(
            stderr,
            exposeExact: false,
            baseUrl: "http://127.0.0.1:1");
        using var duplicateScope = System.Text.Json.JsonDocument.Parse("""{"mode":"default","mode":"all"}""");
        var invalidArguments = new Dictionary<string, object?>[]
        {
            new()
            {
                ["scope"] = new Dictionary<string, object?> { ["mode"] = "default" },
                ["entityKinds"] = new[] { "event" },
                ["unknown"] = true
            },
            new()
            {
                ["scope"] = new Dictionary<string, object?> { ["mode"] = "default", ["unknown"] = true },
                ["entityKinds"] = new[] { "event" }
            },
            new() { ["scope"] = new Dictionary<string, object?> { ["mode"] = "default" } },
            new()
            {
                ["scope"] = new Dictionary<string, object?> { ["mode"] = "default" },
                ["entityKinds"] = "event"
            },
            new()
            {
                ["scope"] = duplicateScope.RootElement,
                ["entityKinds"] = new[] { "event" }
            }
        };

        for (var index = 0; index < invalidArguments.Length; index++)
        {
            var arguments = invalidArguments[index];
            var result = await client.CallToolAsync(
                "calendar_entities.query",
                arguments,
                cancellationToken: TestContext.Current.CancellationToken);
            result.IsError.ShouldBe(true);
            result.StructuredContent.ShouldNotBeNull($"invalid argument case {index}");
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
            result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("schemaLexicalDiscriminator");
        }
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task CalendarResourceGet_ReturnsStrongRevisionAndLosslessPropertiesWithoutRawPayload()
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
        snapshot.TryGetProperty("authoritativePayload", out _).ShouldBeFalse();
        snapshot.ToString().ShouldNotContain(Convert.ToBase64String(observed.Utf8));
        snapshot.GetProperty("calendarProperties").EnumerateArray()
            .Single(property => property.GetProperty("name").GetString() == "X-UNKNOWN")
            .GetProperty("originalSlice").GetString().ShouldBe("X-UNKNOWN;X-P=one,two:kept\r\n");
        snapshot.GetProperty("projection").GetProperty("kind").GetString().ShouldBe("todo");
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldNotContain("BEGIN:VCALENDAR");
        stderr.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(262_144, "invalid_input", "schemaLexicalDiscriminator")]
    [InlineData(262_145, "payload_too_large", "admissionAndPayload")]
    public async Task CalendarResourceGet_NativeSdkEnforcesExactArgumentBoundaryBeforeDispatch(
        int argumentBytes,
        string expectedCode,
        string expectedPhase)
    {
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(stderr, exposeExact: false);
        var arguments = ResourceGetArgumentsAtSize(argumentBytes);
        JsonSerializer.SerializeToUtf8Bytes(arguments).Length.ShouldBe(argumentBytes);

        var result = await client.CallToolAsync(
            "calendar_resources.get",
            arguments,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe(expectedPhase);
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
            "calendar_resources.delete",
            "calendar_resources.exact_get",
            "calendar_resources.exact_create",
            "calendar_resources.exact_replace",
            "calendar_resources.exact_move"
        ]);
        listed.Resources.ShouldBeEmpty();
        read.Contents.ShouldHaveSingleItem().ShouldBeOfType<BlobResourceContents>().DecodedData
            .ToArray().ShouldBe(observed.Utf8);

        await PutResourceAsync("exact-read-1.ics", content.Replace("Exact integration", "Changed revision", StringComparison.Ordinal));
        await Should.ThrowAsync<ModelContextProtocol.McpException>(() =>
            client.ReadResourceAsync(link.Uri, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExactWrites_PreserveCallerResourceAcrossMrtrCreateReplaceAndAtomicMove()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var uid = $"exact-write-{suffix}";
        var calendarHref = $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}";
        var sourceHref = $"{calendarHref}exact-source-{suffix}.ics";
        var destinationHref = $"{calendarHref}exact-destination-{suffix}.ics";
        var createdContent = ExactEvent(uid, "Created", "X-INERT:<script>alert(1)</script>");
        var replacedContent = ExactEvent(uid, "Replaced", "X-INERT:<script>alert(2)</script>");
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(
            stderr,
            exposeExact: true,
            calendarHrefs: calendarHref,
            confirmMutations: true);

        var created = await client.CallToolAsync(
            "calendar_resources.exact_create",
            new Dictionary<string, object?>
            {
                ["destinationHref"] = sourceHref,
                ["utf8Resource"] = createdContent
            },
            cancellationToken: TestContext.Current.CancellationToken);
        var createdRevision = created.StructuredContent!.Value.GetProperty("snapshot")
            .GetProperty("entityRevision");
        var replaced = await client.CallToolAsync(
            "calendar_resources.exact_replace",
            new Dictionary<string, object?>
            {
                ["revision"] = ExactRevisionArguments(createdRevision),
                ["utf8Resource"] = replacedContent
            },
            cancellationToken: TestContext.Current.CancellationToken);
        var replacedRevision = replaced.StructuredContent!.Value.GetProperty("snapshot")
            .GetProperty("entityRevision");
        var moved = await client.CallToolAsync(
            "calendar_resources.exact_move",
            new Dictionary<string, object?>
            {
                ["revision"] = ExactRevisionArguments(replacedRevision),
                ["destinationHref"] = destinationHref
            },
            cancellationToken: TestContext.Current.CancellationToken);

        created.IsError.ShouldBe(false);
        replaced.IsError.ShouldBe(false);
        moved.IsError.ShouldBe(false);
        moved.StructuredContent!.Value.GetProperty("snapshot").GetProperty("resourceRevision")
            .GetProperty("href").GetString().ShouldBe(destinationHref);
        (await GetStatusAsync(sourceHref)).ShouldBe(HttpStatusCode.NotFound);
        var observed = await GetResourceAsync(destinationHref);
        Encoding.UTF8.GetString(observed.Utf8).ShouldContain("SUMMARY:Replaced");
        Encoding.UTF8.GetString(observed.Utf8).ShouldContain("X-INERT:<script>alert(2)</script>");
        stderr.ShouldBeEmpty();
        await DeleteResourceAsync(destinationHref, observed.EntityTag);
    }

    [Fact]
    public async Task ExactCreate_WrongCredentialIsTypedDeniedWithoutWriteOrLeak()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var destinationHref = $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}unauthorized-{suffix}.ics";
        const string wrongPassword = "wrong-password-must-not-leak";
        var stderr = new ConcurrentQueue<string>();
        await using var client = await CreateClientAsync(
            stderr,
            exposeExact: true,
            calendarHrefs: $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}",
            confirmMutations: true,
            password: wrongPassword);

        var tools = await client.ListToolsAsync(new ListToolsRequestParams(), TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync(
            "calendar_resources.exact_create",
            new Dictionary<string, object?>
            {
                ["destinationHref"] = destinationHref,
                ["utf8Resource"] = ExactEvent($"unauthorized-{suffix}", "Denied", "X-INERT:credential-bound")
            },
            cancellationToken: TestContext.Current.CancellationToken);

        tools.Tools.ShouldContain(tool => tool.Name == "calendar_resources.exact_create");
        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("upstream_unauthorized");
        JsonSerializer.Serialize(result).ShouldNotContain(wrongPassword);
        (await GetStatusAsync(destinationHref)).ShouldBe(HttpStatusCode.NotFound);
        stderr.ShouldBeEmpty();
    }

    private async Task<McpClient> CreateClientAsync(
        ConcurrentQueue<string> stderr,
        bool exposeExact,
        string? baseUrl = null,
        string? calendarHrefs = null,
        bool confirmMutations = false,
        string? password = null)
    {
        var environment = CreateEnvironment();
        if (baseUrl is not null)
        {
            environment["CALDAV_URL"] = baseUrl;
            environment["CALDAV_CALENDAR_HREFS"] = $"{baseUrl}/calendars/test/";
        }
        if (calendarHrefs is not null)
            environment["CALDAV_CALENDAR_HREFS"] = calendarHrefs;
        if (password is not null)
            environment["CALDAV_PASSWORD"] = password;
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
        var options = new McpClientOptions
        {
            ProtocolVersion = "2026-07-28",
            DiscoverProbeTimeout = TimeSpan.FromSeconds(10)
        };
        if (confirmMutations)
        {
            options.Handlers = new McpClientHandlers
            {
                ElicitationHandler = (_, _) => ValueTask.FromResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, JsonElement>
                    {
                        ["confirm"] = JsonSerializer.SerializeToElement(true)
                    }
                })
            };
        }
        return await McpClient.CreateAsync(
            transport,
            options,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, object?> PatchArguments(
        ObservedRevision eventRevision,
        string kind,
        string uid,
        string summary) => new()
    {
        ["snapshot"] = new Dictionary<string, object?>
        {
            ["href"] = eventRevision.Href,
            ["entityUid"] = uid,
            ["entityKind"] = kind,
            ["entityTag"] = eventRevision.EntityTag
        },
        ["target"] = new Dictionary<string, object?> { ["scope"] = "master" },
        ["patch"] = new Dictionary<string, object?>
        {
            ["scalars"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["field"] = "summary",
                    ["operation"] = "set",
                    ["value"] = summary
                }
            },
            ["collections"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["field"] = "categories",
                    ["operation"] = "addRemove",
                    ["add"] = new[] { "Patched" }
                }
            }
        }
    };

    private static Dictionary<string, object?> OneOccurrencePatchArguments(
        ObservedRevision revision,
        string uid) => new()
    {
        ["snapshot"] = new Dictionary<string, object?>
        {
            ["href"] = revision.Href,
            ["entityUid"] = uid,
            ["entityKind"] = "event",
            ["entityTag"] = revision.EntityTag
        },
        ["target"] = new Dictionary<string, object?>
        {
            ["scope"] = "one-occurrence",
            ["recurrenceIdentity"] = new Dictionary<string, object?>
            {
                ["value"] = new Dictionary<string, object?>
                {
                    ["kind"] = "utcDateTime",
                    ["value"] = "2026-08-19T13:00:00Z"
                }
            }
        },
        ["patch"] = new Dictionary<string, object?>
        {
            ["scalars"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["field"] = "summary",
                    ["operation"] = "set",
                    ["value"] = "Moved once"
                },
                new Dictionary<string, object?>
                {
                    ["field"] = "start",
                    ["operation"] = "set",
                    ["value"] = new Dictionary<string, object?>
                    {
                        ["kind"] = "utcDateTime",
                        ["value"] = "2026-08-19T16:00:00Z"
                    }
                }
            }
        }
    };

    private static Dictionary<string, object?> BroadPatchArguments(
        ObservedRevision revision,
        string kind,
        string uid,
        string scope,
        string summary,
        string? recurrenceIdentity = null)
    {
        var target = new Dictionary<string, object?> { ["scope"] = scope };
        if (recurrenceIdentity is not null)
        {
            target["recurrenceIdentity"] = new Dictionary<string, object?>
            {
                ["value"] = new Dictionary<string, object?>
                {
                    ["kind"] = "utcDateTime",
                    ["value"] = recurrenceIdentity
                }
            };
        }
        return new Dictionary<string, object?>
        {
            ["snapshot"] = new Dictionary<string, object?>
            {
                ["href"] = revision.Href,
                ["entityUid"] = uid,
                ["entityKind"] = kind,
                ["entityTag"] = revision.EntityTag
            },
            ["target"] = target,
            ["patch"] = new Dictionary<string, object?>
            {
                ["scalars"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["field"] = "summary",
                        ["operation"] = "set",
                        ["value"] = summary
                    }
                }
            }
        };
    }

    private async Task<string> PutResourceAsync(string name, string content)
        => await PutResourceAsync(_fixture.TodoCalendarHref, name, content);

    private async Task<string> PutResourceAsync(string calendarHref, string name, string content)
    {
        using var client = CreateAuthenticatedClient();
        var href = $"{calendarHref}{name}";
        using var response = await client.PutAsync(
            href,
            new StringContent(content, Encoding.UTF8, "text/calendar"),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return $"{_fixture.BaseUrl}{href}";
    }

    private async Task<CallToolResult> CallOccurrenceAsync(McpClient client, string from, string to) =>
        await client.CallToolAsync(
            "calendar_occurrences.query",
            new Dictionary<string, object?>
            {
                ["scope"] = new Dictionary<string, object?>
                {
                    ["mode"] = "selected",
                    ["calendar"] = new Dictionary<string, object?>
                    {
                        ["by"] = "href",
                        ["href"] = $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}"
                    }
                },
                ["from"] = new Dictionary<string, object?> { ["kind"] = "utcDateTime", ["value"] = from },
                ["to"] = new Dictionary<string, object?> { ["kind"] = "utcDateTime", ["value"] = to }
            },
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<CallToolResult> CallOccurrenceMutationAsync(
        McpClient client,
        string operation,
        ObservedRevision revision,
        string uid,
        string identity) => await client.CallToolAsync(
        $"calendar_occurrences.{operation}",
        new Dictionary<string, object?>
        {
            ["snapshot"] = new Dictionary<string, object?>
            {
                ["href"] = revision.Href,
                ["entityUid"] = uid,
                ["entityKind"] = "todo",
                ["entityTag"] = revision.EntityTag
            },
            ["recurrenceIdentity"] = new Dictionary<string, object?>
            {
                ["value"] = new Dictionary<string, object?>
                {
                    ["kind"] = "utcDateTime",
                    ["value"] = identity
                }
            }
        },
        cancellationToken: TestContext.Current.CancellationToken);

    private static Dictionary<string, object?> TodoCompletionArguments(
        ObservedRevision revision,
        string uid) => new()
    {
        ["snapshot"] = new Dictionary<string, object?>
        {
            ["href"] = revision.Href,
            ["entityUid"] = uid,
            ["entityKind"] = "todo",
            ["entityTag"] = revision.EntityTag
        },
        ["recurrenceIdentity"] = new Dictionary<string, object?>
        {
            ["value"] = new Dictionary<string, object?>
            {
                ["kind"] = "utcDateTime",
                ["value"] = "2026-08-19T09:00:00Z"
            }
        }
    };

    private static Dictionary<string, object?> ExactRevisionArguments(JsonElement revision) => new()
    {
        ["href"] = revision.GetProperty("href").GetString(),
        ["entityUid"] = revision.GetProperty("entityUid").GetString(),
        ["entityKind"] = revision.GetProperty("entityKind").GetString(),
        ["entityTag"] = revision.GetProperty("entityTag").GetString()
    };

    private static Dictionary<string, object?> ResourceGetArgumentsAtSize(int argumentBytes)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["href"] = "https://cal.example/events/a.ics",
            ["padding"] = string.Empty
        };
        var fixedBytes = JsonSerializer.SerializeToUtf8Bytes(arguments).Length;
        arguments["padding"] = new string('x', argumentBytes - fixedBytes);
        return arguments;
    }

    private static string ExactEvent(string uid, string summary, string inertLine) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Integration//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\n"
        + $"DTSTART:20260818T120000Z\r\nSUMMARY:{summary}\r\n{inertLine}\r\n"
        + "END:VEVENT\r\nEND:VCALENDAR\r\n";

    private async Task DeleteResourceAsync(string href, string? entityTag = null)
    {
        using var client = CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, href);
        if (entityTag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", entityTag).ShouldBeTrue();
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
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

    private async Task<HttpStatusCode> GetStatusAsync(string href)
    {
        using var client = CreateAuthenticatedClient();
        using var response = await client.GetAsync(href, TestContext.Current.CancellationToken);
        return response.StatusCode;
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
        ["CALDAV_CALENDAR_HREFS"] = $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}"
    };

    private static string Todo(string uid, string temporalLines) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\nBEGIN:VTODO\r\n"
        + $"UID:{uid}\r\nDTSTAMP:20260815T120000Z\r\n{temporalLines}END:VTODO\r\nEND:VCALENDAR\r\n";

    private static string Event(string uid, string temporalLines) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\nBEGIN:VEVENT\r\n"
        + $"UID:{uid}\r\nDTSTAMP:20260815T120000Z\r\n{temporalLines}END:VEVENT\r\nEND:VCALENDAR\r\n";

    private static string RangeTodo() =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Integration//EN\r\n"
        + "BEGIN:VTODO\r\nUID:occurrence-range\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260814T090000Z\r\n"
        + "DUE:20260814T100000Z\r\nRRULE:FREQ=DAILY;COUNT=5\r\nEND:VTODO\r\n"
        + "BEGIN:VTODO\r\nUID:occurrence-range\r\nDTSTAMP:20260815T120000Z\r\n"
        + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260816T090000Z\r\n"
        + "DTSTART:20260816T110000Z\r\nDUE:20260816T120000Z\r\nEND:VTODO\r\n"
        + "BEGIN:VTODO\r\nUID:occurrence-range\r\nDTSTAMP:20260815T120000Z\r\n"
        + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260817T090000Z\r\n"
        + "DTSTART:20260817T130000Z\r\nDUE:20260817T150000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private static ObservedRevision AssertCommittedCreate(
        CallToolResult result,
        string kind,
        string uid,
        string calendarHref)
    {
        result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("outcome").GetString().ShouldBe("success");
        structured.GetProperty("mutationState").GetString().ShouldBe("committed");
        var snapshot = structured.GetProperty("snapshot");
        var revision = snapshot.GetProperty("resourceRevision");
        var href = revision.GetProperty("href").GetString();
        var entityTag = revision.GetProperty("entityTag").GetString();
        href.ShouldNotBeNull();
        entityTag.ShouldNotBeNull();
        entityTag.ShouldStartWith("\"");
        AssertCanonicalDirectChild(calendarHref, href);
        var projection = snapshot.GetProperty("projection");
        projection.GetProperty("kind").GetString().ShouldBe(kind);
        projection.GetProperty("uid").GetString().ShouldBe(uid);
        return new ObservedRevision(href, entityTag);
    }

    private static void AssertCanonicalDirectChild(string calendarHref, string resourceHref)
    {
        var calendar = new Uri(calendarHref, UriKind.Absolute);
        var resource = new Uri(resourceHref, UriKind.Absolute);
        resource.GetLeftPart(UriPartial.Authority).ShouldBe(calendar.GetLeftPart(UriPartial.Authority));
        resource.Query.ShouldBeEmpty();
        resource.Fragment.ShouldBeEmpty();
        resource.UserInfo.ShouldBeEmpty();
        resourceHref.ShouldNotContain("%2F", Case.Insensitive);
        resourceHref.ShouldNotContain("%5C", Case.Insensitive);
        var relative = calendar.MakeRelativeUri(resource).OriginalString;
        relative.ShouldNotBeEmpty();
        relative.ShouldNotContain("/");
        relative.ShouldNotContain("\\");
        new Uri(calendar, relative).AbsoluteUri.ShouldBe(resource.AbsoluteUri);
    }

    private sealed record ObservedRevision(string Href, string EntityTag);

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
