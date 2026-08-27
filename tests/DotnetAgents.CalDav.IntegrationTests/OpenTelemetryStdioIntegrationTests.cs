using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

[Collection("RadicaleCollection")]
public sealed class OpenTelemetryStdioIntegrationTests
{
    private readonly RadicaleFixture _fixture;

    public OpenTelemetryStdioIntegrationTests(RadicaleFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TodoStartContinue_ExportsSafeModulePhasesAndZeroContinuationWireWork()
    {
        const string evaluationTimeZone = "America/Sao_Paulo";
        var suffix = Guid.NewGuid().ToString("N");
        var firstUid = $"private-query-first-{suffix}";
        var secondUid = $"private-query-second-{suffix}";
        var privateSummary = $"Private query snapshot {suffix}";
        var firstHref = await PutResourceAsync(
            _fixture.TodoCalendarHref,
            $"query-first-{suffix}.ics",
            SnapshotTodo(firstUid, $"{privateSummary} first"));
        var secondHref = await PutResourceAsync(
            _fixture.TodoCalendarHref,
            $"query-second-{suffix}.ics",
            SnapshotTodo(secondUid, $"{privateSummary} second"));
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();
        string cursor;
        try
        {
            await using (var client = await CreateClientAsync(
                             receiver.Endpoint,
                             stderr,
                             calendarHrefs: $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}",
                             evaluationTimeZone: evaluationTimeZone))
            {
                var start = await client.CallToolAsync(
                    "todos.query",
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
                            ["value"] = "2026-08-24T03:00:00Z"
                        },
                        ["to"] = new Dictionary<string, object?>
                        {
                            ["kind"] = "utcDateTime",
                            ["value"] = "2026-08-24T04:00:00Z"
                        },
                        ["pageSize"] = 1
                    },
                    cancellationToken: TestContext.Current.CancellationToken);
                start.IsError.ShouldBe(false, start.StructuredContent?.ToString());
                var structured = start.StructuredContent!.Value;
                structured.GetProperty("pagination").GetProperty("mode").GetString()
                    .ShouldBe("query_result_snapshot");
                structured.GetProperty("temporalEvaluationContext").GetRawText()
                    .ShouldBe("{\"timeZone\":\"America/Sao_Paulo\",\"source\":\"configuration\"}");
                cursor = structured.GetProperty("pagination").GetProperty("nextCursor").GetString()
                    .ShouldNotBeNull();

                var continuation = await client.CallToolAsync(
                    "todos.query",
                    new Dictionary<string, object?> { ["cursor"] = cursor, ["pageSize"] = 1 },
                    cancellationToken: TestContext.Current.CancellationToken);
                continuation.IsError.ShouldBe(false, continuation.StructuredContent?.ToString());
                continuation.StructuredContent!.Value.GetProperty("pagination").GetProperty("mode").GetString()
                    .ShouldBe("query_result_snapshot");
                continuation.StructuredContent.Value.GetProperty("temporalEvaluationContext").GetRawText()
                    .ShouldBe(structured.GetProperty("temporalEvaluationContext").GetRawText());
            }

            await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
            var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(15);
            OtlpSpan[] operations;
            do
            {
                operations = OtlpProtobufReader.ReadSpans(receiver.Requests).Where(span =>
                    span.Name == "caldav.operation"
                    && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "todos.query"))
                    .ToArray();
                if (operations.Length == 2)
                    break;
                if (!await receiver.WaitForRequestAsync(TestContext.Current.CancellationToken))
                    break;
            } while (TimeProvider.System.GetUtcNow() < deadline);

            operations.Length.ShouldBe(2);
            var startOperation = operations.Single(span =>
                Equals(span.Attributes.GetValueOrDefault("caldav.query.mode"), "start"));
            var continueOperation = operations.Single(span =>
                Equals(span.Attributes.GetValueOrDefault("caldav.query.mode"), "continue"));
            startOperation.Attributes.GetValueOrDefault("caldav.query.fetch_mode").ShouldBe("multiget");
            Convert.ToInt64(startOperation.Attributes["caldav.query.multiget_resource_count"])
                .ShouldBeGreaterThan(0);
            startOperation.Attributes.GetValueOrDefault("caldav.query.direct_get_resource_count").ShouldBe(0L);
            startOperation.Attributes.GetValueOrDefault("caldav.query.direct_get_attempt_count").ShouldBe(0L);
            startOperation.Attributes.GetValueOrDefault("caldav.query.parse_count").ShouldBeNull();
            var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
            var startTrace = spans.Where(span => span.TraceId.SequenceEqual(startOperation.TraceId)).ToArray();
            var continueTrace = spans.Where(span => span.TraceId.SequenceEqual(continueOperation.TraceId)).ToArray();
            startTrace.ShouldContain(span => span.ScopeName == "DotnetAgents.CalDav.Http");
            startTrace.ShouldNotContain(span => span.ScopeName == "DotnetAgents.CalDav.Http"
                && span.Name == "GET");
            continueTrace.ShouldNotContain(span => span.ScopeName == "DotnetAgents.CalDav.Http");
            continueTrace.Where(span => span.Name.StartsWith("caldav.query.phase.", StringComparison.Ordinal))
                .ShouldAllBe(span => span.ParentSpanId.SequenceEqual(continueOperation.SpanId));
            continueTrace.Where(span => span.Name.StartsWith("caldav.query.phase.", StringComparison.Ordinal))
                .Select(span => span.Attributes.GetValueOrDefault("caldav.query.phase"))
                .OrderBy(value => value)
                .ShouldBe(new object?[] { "page_admission", "snapshot_lookup" });
            continueOperation.Attributes.Where(attribute =>
                    attribute.Key.StartsWith("caldav.query.", StringComparison.Ordinal))
                .Select(attribute => attribute.Key)
                .Order(StringComparer.Ordinal)
                .ShouldBe([
                    "caldav.query.mode",
                    "caldav.query.page_admission_count",
                    "caldav.query.snapshot_lookup_count"
                ]);
            OtlpProtobufReader.ContainsUtf8(receiver.Requests, cursor).ShouldBeFalse();
            OtlpProtobufReader.ContainsUtf8(receiver.Requests, evaluationTimeZone).ShouldBeFalse();
            foreach (var privateValue in new[] { firstUid, secondUid, privateSummary, firstHref, secondHref })
                OtlpProtobufReader.ContainsUtf8(receiver.Requests, privateValue).ShouldBeFalse();
            spans.ShouldAllBe(span => span.EventCount == 0);
            stderr.ShouldBeEmpty();
        }
        finally
        {
            await DeleteResourceAsync(firstHref, entityTag: null);
            await DeleteResourceAsync(secondHref, entityTag: null);
        }
    }

    [Fact]
    public async Task QueryCompatibilityMode_ExportsMixedAndCachedDirectGetTruthOverBuiltStdio()
    {
        await using var server = new QueryCompatibilityServer();
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();
        await using (var client = await CreateClientAsync(
                         receiver.Endpoint,
                         stderr,
                         baseUrl: server.BaseUrl,
                         calendarHrefs: $"{server.MultigetCalendarHref},{server.DirectCalendarHref}"))
        {
            var mixed = await client.CallToolAsync(
                "calendar_entities.query",
                StartEntityQueryArguments(pageSize: 1),
                cancellationToken: TestContext.Current.CancellationToken);
            mixed.IsError.ShouldBe(false, mixed.StructuredContent?.ToString());
            mixed.StructuredContent!.Value.GetProperty("items").GetArrayLength().ShouldBe(1);
            var cursor = mixed.StructuredContent.Value.GetProperty("pagination").GetProperty("nextCursor")
                .GetString().ShouldNotBeNull();

            var continuation = await client.CallToolAsync(
                "calendar_entities.query",
                new Dictionary<string, object?> { ["cursor"] = cursor, ["pageSize"] = 1 },
                cancellationToken: TestContext.Current.CancellationToken);
            continuation.IsError.ShouldBe(false, continuation.StructuredContent?.ToString());
            continuation.StructuredContent!.Value.GetProperty("items").GetArrayLength().ShouldBe(1);

            var direct = await client.CallToolAsync(
                "calendar_entities.query",
                StartEntityQueryArguments(server.DirectCalendarHref),
                cancellationToken: TestContext.Current.CancellationToken);
            direct.IsError.ShouldBe(false, direct.StructuredContent?.ToString());
            direct.StructuredContent!.Value.GetProperty("items").GetArrayLength().ShouldBe(1);
        }

        await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
        var operations = await WaitForQueryOperationsAsync(receiver, expectedCount: 3);
        operations.Length.ShouldBe(3);
        var mixedOperation = operations.Single(span =>
            Equals(span.Attributes.GetValueOrDefault("caldav.query.fetch_mode"), "mixed"));
        var directOperation = operations.Single(span =>
            Equals(span.Attributes.GetValueOrDefault("caldav.query.fetch_mode"), "direct_get_fallback"));
        var continueOperation = operations.Single(span =>
            Equals(span.Attributes.GetValueOrDefault("caldav.query.mode"), "continue"));
        AssertFallbackOperation(mixedOperation, multigetResourceCount: 3);
        AssertFallbackOperation(directOperation, multigetResourceCount: null);
        var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
        var mixedTrace = spans.Where(span => span.TraceId.SequenceEqual(mixedOperation.TraceId)).ToArray();
        var directTrace = spans.Where(span => span.TraceId.SequenceEqual(directOperation.TraceId)).ToArray();
        var continueTrace = spans.Where(span => span.TraceId.SequenceEqual(continueOperation.TraceId)).ToArray();
        mixedTrace.ShouldContain(span => span.ScopeName == "DotnetAgents.CalDav.Http"
            && span.Name == "REPORT"
            && Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 501L)
            && span.StatusCode == 2);
        AssertDirectReadTrace(mixedTrace);
        AssertDirectReadTrace(directTrace);
        continueTrace.ShouldNotContain(span => span.ScopeName == "DotnetAgents.CalDav.Http");
        continueOperation.Attributes.Where(attribute =>
                attribute.Key.StartsWith("caldav.query.", StringComparison.Ordinal))
            .Select(attribute => attribute.Key)
            .Order(StringComparer.Ordinal)
            .ShouldBe([
                "caldav.query.mode",
                "caldav.query.page_admission_count",
                "caldav.query.snapshot_lookup_count"
            ]);
        server.MultigetReportCount.ShouldBe(2);
        server.DirectGetCount.ShouldBe(4);
        stderr.ShouldBeEmpty();
        foreach (var privateValue in server.PrivateValues)
            OtlpProtobufReader.ContainsUtf8(receiver.Requests, privateValue).ShouldBeFalse();
        spans.ShouldAllBe(span => span.EventCount == 0);
    }

    [Fact]
    public async Task OptIn_ExportsSafeParentedWaterfallLogsAndMcpMetricsOverLoopbackOtlp()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var recurringUid = $"private-recurring-{suffix}";
        var createdUid = $"private-created-{suffix}";
        var privateSummary = $"Private telemetry summary {suffix}";
        var privateCalendarName = $"Private Calendar Name {suffix}";
        var recurringHref = await PutResourceAsync(
            _fixture.TodoCalendarHref,
            $"{recurringUid}.ics",
            RecurringTodo(recurringUid));
        string? createdHref = null;
        string? createdEntityTag = null;
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        try
        {
            string? nextCursor;
            await using (var client = await CreateClientAsync(
                             receiver.Endpoint,
                             stderr,
                             defaultTodoCalendarName: privateCalendarName))
            {
                var query = await client.CallToolAsync(
                    "calendar_occurrences.query",
                    OccurrenceQueryArguments(),
                    cancellationToken: TestContext.Current.CancellationToken);
                query.IsError.ShouldBe(false, query.StructuredContent?.ToString());
                nextCursor = query.StructuredContent!.Value.GetProperty("pagination")
                    .GetProperty("nextCursor")
                    .GetString()
                    .ShouldNotBeNull();
                var continuation = await client.CallToolAsync(
                    "calendar_occurrences.query",
                    new Dictionary<string, object?> { ["cursor"] = nextCursor, ["pageSize"] = 1 },
                    cancellationToken: TestContext.Current.CancellationToken);
                continuation.IsError.ShouldBe(false, continuation.StructuredContent?.ToString());
                continuation.StructuredContent!.Value.GetProperty("pagination").GetProperty("mode").GetString()
                    .ShouldBe("query_result_snapshot");

                var created = await client.CallToolAsync(
                    "events.create",
                    CreateEventArguments(createdUid, privateSummary),
                    cancellationToken: TestContext.Current.CancellationToken);
                created.IsError.ShouldBe(false, created.StructuredContent?.ToString());
                var revision = created.StructuredContent!.Value.GetProperty("snapshot")
                    .GetProperty("resourceRevision");
                createdHref = revision.GetProperty("href").GetString();
                createdEntityTag = revision.GetProperty("entityTag").GetString();
            }

            await receiver.WaitForPathsAsync(
                ["/v1/traces", "/v1/metrics", "/v1/logs"],
                TestContext.Current.CancellationToken);

            AssertTraceWaterfalls(receiver.Requests);
            AssertLogsAndMetrics(receiver.Requests);
            AssertProhibitedDataAbsent(
                receiver.Requests,
                recurringUid,
                createdUid,
                privateSummary,
                privateCalendarName,
                recurringHref,
                createdHref!,
                createdEntityTag!,
                nextCursor);
            stderr.ShouldBeEmpty();
        }
        finally
        {
            if (createdHref is not null)
                await DeleteResourceAsync(createdHref, createdEntityTag);
            await DeleteResourceAsync(recurringHref, entityTag: null);
        }
    }

    [Fact]
    public async Task SemanticMove_ExportsBoundedDispatchReconciliationAndExpectedAbsenceOverStdio()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var uid = $"private-move-{suffix}";
        var content = PrivateTodo(uid);
        var sourceHref = await PutResourceAsync(_fixture.TodoCalendarHref, $"source-{suffix}.ics", content);
        var sourceEntityTag = await GetEntityTagAsync(sourceHref);
        var destinationCalendarHref = $"{_fixture.BaseUrl}{_fixture.ShoppingCalendarHref}";
        string? destinationHref = null;
        string? destinationEntityTag = null;
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        try
        {
            await using (var client = await CreateClientAsync(
                             receiver.Endpoint,
                             stderr,
                             calendarHrefs: $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref},{destinationCalendarHref}"))
            {
                var result = await client.CallToolAsync(
                    "calendar_resources.move",
                    MoveArguments(sourceHref, uid, sourceEntityTag, destinationCalendarHref),
                    cancellationToken: TestContext.Current.CancellationToken);
                result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
                var revision = result.StructuredContent!.Value.GetProperty("snapshot")
                    .GetProperty("resourceRevision");
                destinationHref = revision.GetProperty("href").GetString().ShouldNotBeNull();
                destinationEntityTag = revision.GetProperty("entityTag").GetString().ShouldNotBeNull();
            }

            await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
            var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
            var operation = spans.Single(span =>
                span.Name == "caldav.operation"
                && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendar_resources.move"));
            operation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("success");
            operation.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBe("committed");
            operation.Attributes.GetValueOrDefault("caldav.move.dispatch").ShouldBe("dispatched");
            operation.Attributes.GetValueOrDefault("caldav.move.collision").ShouldBe("none");
            operation.Attributes.GetValueOrDefault("caldav.move.reconciliation")
                .ShouldBe("faithful_destination_source_absent");
            operation.Attributes.GetValueOrDefault("error.type").ShouldBeNull();
            operation.StatusCode.ShouldBe(0);
            spans.Count(span =>
                    span.ScopeName == "DotnetAgents.CalDav.Http"
                    && Equals(span.Attributes.GetValueOrDefault("http.request.method"), "MOVE"))
                .ShouldBe(1);
            var expectedAbsence = spans.Where(span =>
                span.ScopeName == "DotnetAgents.CalDav.Http"
                && Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 404L))
                .ToArray();
            expectedAbsence.Length.ShouldBe(2);
            expectedAbsence.ShouldAllBe(span =>
                Equals(span.Attributes.GetValueOrDefault("caldav.http.request_purpose"), "absence_probe")
                && Equals(span.Attributes.GetValueOrDefault("caldav.http.observation"), "expected_absence")
                && span.Attributes.GetValueOrDefault("error.type") == null
                && span.StatusCode == 1);
            spans.ShouldAllBe(span => span.EventCount == 0);
            AssertProhibitedDataAbsent(
                receiver.Requests,
                uid,
                content,
                sourceHref,
                sourceEntityTag,
                destinationHref,
                destinationEntityTag);
            stderr.ShouldBeEmpty();
        }
        finally
        {
            await DeleteResourceAsync(sourceHref, entityTag: null);
            if (destinationHref is not null)
                await DeleteResourceAsync(destinationHref, destinationEntityTag);
        }
    }

    [Fact]
    public async Task SemanticMoveUidConflict_ExportsDefiniteRejectionWithoutPrivateValuesOverStdio()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var uid = $"private-move-conflict-{suffix}";
        var content = PrivateTodo(uid);
        var sourceHref = await PutResourceAsync(_fixture.TodoCalendarHref, $"source-conflict-{suffix}.ics", content);
        var collisionHref = await PutResourceAsync(
            _fixture.ShoppingCalendarHref,
            $"existing-conflict-{suffix}.ics",
            content);
        var sourceEntityTag = await GetEntityTagAsync(sourceHref);
        var destinationCalendarHref = $"{_fixture.BaseUrl}{_fixture.ShoppingCalendarHref}";
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        try
        {
            await using (var client = await CreateClientAsync(
                             receiver.Endpoint,
                             stderr,
                             calendarHrefs: $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref},{destinationCalendarHref}"))
            {
                var result = await client.CallToolAsync(
                    "calendar_resources.move",
                    MoveArguments(sourceHref, uid, sourceEntityTag, destinationCalendarHref),
                    cancellationToken: TestContext.Current.CancellationToken);
                result.IsError.ShouldBe(true);
                result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("conflict");
                result.StructuredContent!.Value.GetProperty("mutationState").GetString().ShouldBe("not_committed");
            }

            await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
            var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
            var operation = spans.Single(span =>
                span.Name == "caldav.operation"
                && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendar_resources.move"));
            operation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("error");
            operation.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBe("not_committed");
            operation.Attributes.GetValueOrDefault("caldav.move.dispatch").ShouldBe("rejected");
            operation.Attributes.GetValueOrDefault("caldav.move.collision").ShouldBe("uid");
            operation.Attributes.GetValueOrDefault("caldav.move.reconciliation").ShouldBe("not_run");
            operation.Attributes.GetValueOrDefault("caldav.error.phase").ShouldBe("execution");
            spans.Count(span =>
                    span.ScopeName == "DotnetAgents.CalDav.Http"
                    && Equals(span.Attributes.GetValueOrDefault("http.request.method"), "MOVE"))
                .ShouldBe(1);
            spans.ShouldContain(span =>
                Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 404L)
                && Equals(span.Attributes.GetValueOrDefault("caldav.http.observation"), "expected_absence"));
            spans.ShouldAllBe(span => span.EventCount == 0);
            AssertProhibitedDataAbsent(
                receiver.Requests,
                uid,
                content,
                sourceHref,
                sourceEntityTag,
                collisionHref);
            stderr.ShouldBeEmpty();
        }
        finally
        {
            await DeleteResourceAsync(sourceHref, entityTag: null);
            await DeleteResourceAsync(collisionHref, entityTag: null);
        }
    }

    [Fact]
    public async Task SemanticMovePossiblyDispatched_ExportsReconciledTruthOverStdio()
    {
        await using var server = new PossiblyDispatchedMoveServer();
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        await using (var client = await CreateClientAsync(
                         receiver.Endpoint,
                         stderr,
                         baseUrl: server.BaseUrl,
                         calendarHrefs: $"{server.SourceCalendarHref},{server.DestinationCalendarHref}"))
        {
            var result = await client.CallToolAsync(
                "calendar_resources.move",
                MoveArguments(
                    server.SourceHref,
                    PossiblyDispatchedMoveServer.PrivateUid,
                    PossiblyDispatchedMoveServer.SourceEntityTag,
                    server.DestinationCalendarHref),
                cancellationToken: TestContext.Current.CancellationToken);
            result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
            result.StructuredContent!.Value.GetProperty("mutationState").GetString().ShouldBe("committed");
        }

        await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
        var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
        var operation = spans.Single(span =>
            span.Name == "caldav.operation"
            && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendar_resources.move"));
        operation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("success");
        operation.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBe("committed");
        operation.Attributes.GetValueOrDefault("caldav.move.dispatch").ShouldBe("possibly_dispatched");
        operation.Attributes.GetValueOrDefault("caldav.move.collision").ShouldBe("none");
        operation.Attributes.GetValueOrDefault("caldav.move.reconciliation")
            .ShouldBe("faithful_destination_source_absent");
        spans.Count(span =>
                span.ScopeName == "DotnetAgents.CalDav.Http"
                && Equals(span.Attributes.GetValueOrDefault("http.request.method"), "MOVE"))
            .ShouldBe(1);
        spans.Count(span =>
                Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 404L)
                && Equals(span.Attributes.GetValueOrDefault("caldav.http.observation"), "expected_absence"))
            .ShouldBe(2);
        spans.ShouldAllBe(span => span.EventCount == 0);
        AssertProhibitedDataAbsent(
            receiver.Requests,
            PossiblyDispatchedMoveServer.PrivateUid,
            PossiblyDispatchedMoveServer.PrivateContent,
            PossiblyDispatchedMoveServer.SourceEntityTag,
            server.SourceHref,
            server.DestinationHref);
        server.MoveCount.ShouldBe(1);
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExactMoveMrtr_ExportsConstantFreshReviewAndOneMoveOverStdio()
    {
        await using var server = new PossiblyDispatchedMoveServer();
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        await using (var client = await CreateClientAsync(
                         receiver.Endpoint,
                         stderr,
                         baseUrl: server.BaseUrl,
                         calendarHrefs: $"{server.SourceCalendarHref},{server.DestinationCalendarHref}",
                         exposeExact: true,
                         confirmMutations: true))
        {
            var result = await client.CallToolAsync(
                "calendar_resources.exact_move",
                ExactMoveArguments(
                    server.SourceHref,
                    PossiblyDispatchedMoveServer.PrivateUid,
                    PossiblyDispatchedMoveServer.SourceEntityTag,
                    server.DestinationHref),
                cancellationToken: TestContext.Current.CancellationToken);
            result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
            result.StructuredContent!.Value.GetProperty("mutationState").GetString().ShouldBe("committed");
            result.StructuredContent.Value.GetProperty("snapshot").GetProperty("resourceRevision")
                .GetProperty("href").GetString().ShouldBe(server.DestinationHref);
        }

        await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
        var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
        AssertExactMoveMrtrTelemetry(spans);
        AssertProhibitedDataAbsent(
            receiver.Requests,
            PossiblyDispatchedMoveServer.PrivateUid,
            PossiblyDispatchedMoveServer.PrivateContent,
            PossiblyDispatchedMoveServer.SourceEntityTag,
            server.SourceHref,
            server.DestinationHref);
        server.GetCount.ShouldBe(6);
        server.ReportCount.ShouldBe(0);
        server.MoveCount.ShouldBe(1);
        stderr.ShouldBeEmpty();
    }

    private static void AssertExactMoveMrtrTelemetry(IReadOnlyList<OtlpSpan> spans)
    {
        var operations = spans.Where(span =>
            span.Name == "caldav.operation"
            && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendar_resources.exact_move"))
            .ToArray();
        var initial = operations.Single(operation =>
            Equals(operation.Attributes.GetValueOrDefault("caldav.outcome"), "input_required"));
        initial.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBe("not_attempted");
        initial.Attributes.GetValueOrDefault("caldav.move.dispatch").ShouldBe("not_attempted");
        initial.Attributes.GetValueOrDefault("caldav.move.reconciliation").ShouldBe("not_run");
        var confirmed = operations.Single(operation =>
            Equals(operation.Attributes.GetValueOrDefault("caldav.outcome"), "success"));
        confirmed.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBe("committed");
        confirmed.Attributes.GetValueOrDefault("caldav.move.dispatch").ShouldBe("possibly_dispatched");
        confirmed.Attributes.GetValueOrDefault("caldav.move.collision").ShouldBe("none");
        confirmed.Attributes.GetValueOrDefault("caldav.move.reconciliation")
            .ShouldBe("faithful_destination_source_absent");
        spans.Count(span =>
                span.ScopeName == "DotnetAgents.CalDav.Http"
                && Equals(span.Attributes.GetValueOrDefault("http.request.method"), "MOVE"))
            .ShouldBe(1);
        spans.Count(span =>
                span.ScopeName == "DotnetAgents.CalDav.Http"
                && Equals(span.Attributes.GetValueOrDefault("http.request.method"), "GET")
                && Equals(span.Attributes.GetValueOrDefault("caldav.http.request_purpose"), "absence_probe")
                && Equals(span.Attributes.GetValueOrDefault("caldav.http.observation"), "expected_absence"))
            .ShouldBe(3);
        spans.ShouldAllBe(span => span.EventCount == 0);
    }

    [Fact]
    public async Task InvalidClientDimensionsTraceStateAndPayloadMarkersAreRedacted()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var privateMethod = $"private-exception-message-{suffix}";
        var privateTool = $"private-tool-url-uid-{suffix}";
        var privateCalendarName = $"private-calendar-name-{suffix}";
        var privateCursor = $"private-cursor-{suffix}";
        var privateMrtrHandle = $"private-mrtr-handle-{suffix}";
        var privateTraceState = $"privatevendor=private-trace-state-{suffix}";
        var reviewedUid = $"private-reviewed-mrtr-{suffix}";
        var reviewedHref = await PutResourceAsync(
            _fixture.TodoCalendarHref,
            $"{reviewedUid}.ics",
            PrivateTodo(reviewedUid));
        var reviewedEntityTag = await GetEntityTagAsync(reviewedHref);
        await using var receiver = OtlpLoopbackReceiver.Start();
        using var process = CreateRawTelemetryProcess(receiver.Endpoint);
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        try
        {
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"privacy-test\",\"version\":\"1\"}}}");
            _ = await ReadRawResponseAsync(process, 1);
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await WriteRawAsync(process, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = privateMethod,
                @params = new
                {
                    _meta = new Dictionary<string, object?>
                    {
                        ["traceparent"] = "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01",
                        ["tracestate"] = privateTraceState
                    }
                }
            }));
            _ = await ReadRawResponseAsync(process, 2);
            await WriteRawAsync(process, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    _meta = new Dictionary<string, object?>
                    {
                        ["traceparent"] = "00-1123456789abcdef0123456789abcdef-1123456789abcdef-01",
                        ["tracestate"] = privateTraceState
                    },
                    name = privateTool,
                    arguments = new
                    {
                        scope = new { mode = "selected", calendar = new { by = "name", name = privateCalendarName } },
                        cursor = privateCursor
                    },
                    requestState = privateMrtrHandle
                }
            }));
            _ = await ReadRawResponseAsync(process, 3);
            await WriteRawAsync(process, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new
                {
                    _meta = new Dictionary<string, object?>
                    {
                        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                        ["io.modelcontextprotocol/clientInfo"] = new { name = "privacy-test", version = "1" },
                        ["io.modelcontextprotocol/clientCapabilities"] = new { }
                    },
                    name = "calendar_resources.delete",
                    arguments = new
                    {
                        revision = new
                        {
                            href = reviewedHref,
                            entityUid = reviewedUid,
                            entityKind = "todo",
                            entityTag = reviewedEntityTag
                        }
                    }
                }
            }));
            var inputRequired = (await ReadRawResponseAsync(process, 4)).GetProperty("result");
            inputRequired.GetProperty("inputRequests").TryGetProperty("confirm_delete", out _)
                .ShouldBeTrue();
            var actualMrtrHandle = inputRequired.GetProperty("requestState").GetString()
                .ShouldNotBeNull();
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            await receiver.WaitForPathsAsync(
                ["/v1/traces", "/v1/metrics"],
                TestContext.Current.CancellationToken);
            var privateValues = new[]
            {
                privateMethod,
                privateTool,
                privateCalendarName,
                privateCursor,
                privateMrtrHandle,
                privateTraceState,
                reviewedUid,
                reviewedHref,
                reviewedEntityTag,
                actualMrtrHandle
            };
            privateValues.ShouldAllBe(value => !OtlpProtobufReader.ContainsUtf8(receiver.Requests, value));
            var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
            spans.ShouldAllBe(span => span.TraceState.Length == 0);
            var operation = spans.Single(span =>
                span.Name == "caldav.operation"
                && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendar_resources.delete"));
            operation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("input_required");
            operation.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBe("not_attempted");
            operation.Attributes.GetValueOrDefault("error.type").ShouldBeNull();
            operation.StatusCode.ShouldBe(0);
            spans.ShouldAllBe(span => span.EventCount == 0);
            var operationMetrics = OtlpProtobufReader.ReadMetrics(receiver.Requests)
                .Where(metric => metric.Name == "mcp.server.operation.duration")
                .ToArray();
            operationMetrics.ShouldNotBeEmpty();
            operationMetrics.SelectMany(metric => metric.DataPointAttributes)
                .SelectMany(attributes => attributes.Keys)
                .ShouldAllBe(key => key == "rpc.response.status_code");
            (await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
            (await stderrTask).ShouldBeEmpty();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await DeleteResourceAsync(reviewedHref, reviewedEntityTag);
        }
    }

    [Fact]
    public async Task ConfirmedDelete_ExportsExpectedAbsenceWithoutErrorOverStdio()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var privateUid = $"private-delete-{suffix}";
        var privateContent = PrivateTodo(privateUid);
        var privateHref = await PutResourceAsync(
            _fixture.TodoCalendarHref,
            $"{privateUid}.ics",
            privateContent);
        var privateEntityTag = await GetEntityTagAsync(privateHref);
        var ordinaryMissingHref =
            $"{_fixture.BaseUrl}{_fixture.TodoCalendarHref}ordinary-missing-{suffix}.ics";
        await using var receiver = OtlpLoopbackReceiver.Start();
        using var process = CreateRawTelemetryProcess(receiver.Endpoint);
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        try
        {
            var arguments = new
            {
                revision = new
                {
                    href = privateHref,
                    entityUid = privateUid,
                    entityKind = "todo",
                    entityTag = privateEntityTag
                }
            };
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"telemetry-delete\",\"version\":\"1\"}}}");
            _ = await ReadRawResponseAsync(process, 1);
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await WriteRawAsync(process, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = "calendar_resources.get",
                    arguments = new { href = ordinaryMissingHref }
                }
            }));
            var ordinaryMissing = (await ReadRawResponseAsync(process, 2)).GetProperty("result");
            ordinaryMissing.GetProperty("isError").GetBoolean().ShouldBeTrue(ordinaryMissing.ToString());
            await WriteRawAsync(process, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    _meta = new Dictionary<string, object?>
                    {
                        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                        ["io.modelcontextprotocol/clientInfo"] = new { name = "telemetry-delete", version = "1" },
                        ["io.modelcontextprotocol/clientCapabilities"] = new { }
                    },
                    name = "calendar_resources.delete",
                    arguments
                }
            }));
            var review = (await ReadRawResponseAsync(process, 3)).GetProperty("result");
            review.GetProperty("inputRequests").TryGetProperty("confirm_delete", out _)
                .ShouldBeTrue(review.ToString());
            var requestState = review.GetProperty("requestState").GetString()
                .ShouldNotBeNull();
            await WriteRawAsync(process, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new
                {
                    _meta = new Dictionary<string, object?>
                    {
                        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                        ["io.modelcontextprotocol/clientInfo"] = new { name = "telemetry-delete", version = "1" },
                        ["io.modelcontextprotocol/clientCapabilities"] = new { }
                    },
                    name = "calendar_resources.delete",
                    arguments,
                    requestState,
                    inputResponses = new Dictionary<string, object?>
                    {
                        ["confirm_delete"] = new
                        {
                            action = "accept",
                            content = new { confirm = true }
                        }
                    }
                }
            }));
            var confirmed = (await ReadRawResponseAsync(process, 4)).GetProperty("result");
            confirmed.GetProperty("isError").GetBoolean().ShouldBeFalse(confirmed.ToString());
            confirmed.GetProperty("structuredContent").GetProperty("outcome").GetString()
                .ShouldBe("success");
            confirmed.GetProperty("structuredContent").GetProperty("mutationState").GetString()
                .ShouldBe("committed");
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
            var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
            var operation = spans.Single(span =>
                span.Name == "caldav.operation"
                && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendar_resources.delete")
                && Equals(span.Attributes.GetValueOrDefault("caldav.outcome"), "success"));
            operation.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBe("committed");
            operation.Attributes.GetValueOrDefault("error.type").ShouldBeNull();
            var httpAttempts = spans.Where(span => span.ScopeName == "DotnetAgents.CalDav.Http").ToArray();
            httpAttempts.Count(span => Equals(span.Attributes.GetValueOrDefault("http.request.method"), "DELETE"))
                .ShouldBe(1);
            var verification = httpAttempts.Single(span =>
                Equals(span.Attributes.GetValueOrDefault("http.request.method"), "GET")
                && Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 404L)
                && Equals(
                    span.Attributes.GetValueOrDefault("caldav.http.request_purpose"),
                    "absence_probe"));
            verification.Attributes.GetValueOrDefault("caldav.http.request_purpose").ShouldBe("absence_probe");
            verification.Attributes.GetValueOrDefault("caldav.http.observation").ShouldBe("expected_absence");
            verification.Attributes.GetValueOrDefault("error.type").ShouldBeNull();
            verification.StatusCode.ShouldBe(1);
            var ordinaryNotFound = httpAttempts.Single(span =>
                Equals(span.Attributes.GetValueOrDefault("http.request.method"), "GET")
                && Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 404L)
                && span.Attributes.GetValueOrDefault("caldav.http.request_purpose") is null);
            ordinaryNotFound.Attributes.GetValueOrDefault("caldav.http.observation").ShouldBeNull();
            ordinaryNotFound.Attributes.GetValueOrDefault("error.type").ShouldBe("404");
            ordinaryNotFound.StatusCode.ShouldBe(2);
            var ordinaryReads = httpAttempts.Where(span =>
                    Equals(span.Attributes.GetValueOrDefault("http.request.method"), "GET")
                    && Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 200L))
                .ToArray();
            ordinaryReads.ShouldNotBeEmpty();
            ordinaryReads.ShouldAllBe(span =>
                span.Attributes.GetValueOrDefault("caldav.http.request_purpose") == null);
            spans.ShouldAllBe(span => span.EventCount == 0);
            AssertProhibitedDataAbsent(
                receiver.Requests,
                privateUid,
                privateHref,
                ordinaryMissingHref,
                privateContent,
                privateEntityTag,
                requestState);
            (await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
            (await stderrTask).ShouldBeEmpty();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await DeleteResourceAsync(privateHref, entityTag: null);
        }
    }

    [Fact]
    public async Task ExactCreateReview_ExportsExpectedAbsenceAndInputRequiredOverRawStdio()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var privateUid = $"private-exact-review-{suffix}";
        var privateHref = $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}private-exact-{suffix}.ics";
        var privateContent = ExactEvent(privateUid, $"Private exact payload {suffix}");
        await using var receiver = OtlpLoopbackReceiver.Start();
        using var process = CreateRawTelemetryProcess(receiver.Endpoint, exposeExact: true);
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        try
        {
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"telemetry-review\",\"version\":\"1\"}}}");
            _ = await ReadRawResponseAsync(process, 1);
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await WriteRawAsync(process, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    _meta = new Dictionary<string, object?>
                    {
                        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                        ["io.modelcontextprotocol/clientInfo"] = new { name = "telemetry-review", version = "1" },
                        ["io.modelcontextprotocol/clientCapabilities"] = new { }
                    },
                    name = "calendar_resources.exact_create",
                    arguments = new
                    {
                        destinationHref = privateHref,
                        utf8Resource = privateContent
                    }
                }
            }));
            var response = await ReadRawResponseAsync(process, 2);
            response.TryGetProperty("result", out var inputRequired)
                .ShouldBeTrue(response.ToString());
            inputRequired.GetProperty("inputRequests").TryGetProperty("confirm_exact_write", out _)
                .ShouldBeTrue(inputRequired.ToString());
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
            var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
            var operation = spans.Single(span =>
                span.Name == "caldav.operation"
                && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendar_resources.exact_create"));
            operation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("input_required");
            operation.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBe("not_attempted");
            operation.Attributes.GetValueOrDefault("error.type").ShouldBeNull();
            operation.StatusCode.ShouldBe(0);
            var expectedAbsence = spans.Single(span =>
                span.ScopeName == "DotnetAgents.CalDav.Http"
                && Equals(span.Attributes.GetValueOrDefault("caldav.http.request_purpose"), "absence_probe"));
            expectedAbsence.Attributes.GetValueOrDefault("http.response.status_code").ShouldBe(404L);
            expectedAbsence.Attributes.GetValueOrDefault("caldav.http.observation").ShouldBe("expected_absence");
            expectedAbsence.Attributes.GetValueOrDefault("error.type").ShouldBeNull();
            expectedAbsence.StatusCode.ShouldBe(1);
            spans.ShouldAllBe(span => span.EventCount == 0);
            OtlpProtobufReader.ContainsUtf8(receiver.Requests, privateUid).ShouldBeFalse();
            OtlpProtobufReader.ContainsUtf8(receiver.Requests, privateHref).ShouldBeFalse();
            OtlpProtobufReader.ContainsUtf8(receiver.Requests, privateContent).ShouldBeFalse();
            (await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
            (await stderrTask).ShouldBeEmpty();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task TransientReadFailure_ExportsDistinctSafeHttpAttempts()
    {
        await using var server = new RetryingDiscoveryServer();
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        await using (var client = await CreateClientAsync(
                         receiver.Endpoint,
                         stderr,
                         baseUrl: server.BaseUrl,
                         calendarHrefs: server.CalendarHref))
        {
            var result = await client.CallToolAsync(
                "calendars.list",
                cancellationToken: TestContext.Current.CancellationToken);
            result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
        }

        await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
        var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
        var discoveryPhases = spans.Where(span => span.Name == "caldav.phase.discovery").ToArray();
        var attempts = spans.Where(span =>
            span.ScopeName == "DotnetAgents.CalDav.Http"
            && Equals(span.Attributes.GetValueOrDefault("http.request.method"), "PROPFIND"))
            .ToArray();
        attempts.Length.ShouldBeGreaterThanOrEqualTo(2, string.Join(
            Environment.NewLine,
            spans.Select(span => $"{span.ScopeName} | {span.Name} | {string.Join(',', span.Attributes.Select(item => $"{item.Key}={item.Value}"))}")));
        attempts.Select(span => Convert.ToHexString(span.SpanId)).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(attempts.Length);
        attempts.ShouldContain(span =>
            Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 503L)
            && span.StatusCode == 2);
        attempts.ShouldContain(span =>
            Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 207L)
            && span.StatusCode == 0);
        attempts.ShouldAllBe(attempt => discoveryPhases.Any(phase =>
            attempt.ParentSpanId.SequenceEqual(phase.SpanId)));
        var operation = spans.Single(span =>
            span.Name == "caldav.operation"
            && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendars.list"));
        operation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("success");
        operation.Attributes.GetValueOrDefault("caldav.transport.recovered").ShouldBe(
            true,
            string.Join(Environment.NewLine, attempts.Select(attempt => string.Join(
                ",",
                attempt.Attributes.Select(item => $"{item.Key}={item.Value}")))));
        operation.Attributes.GetValueOrDefault("caldav.transport.retry_count").ShouldBe(1L);
        operation.StatusCode.ShouldBe(0);
        attempts.ShouldContain(attempt =>
            Equals(attempt.Attributes.GetValueOrDefault("http.request.resend_count"), 1L));
        spans.ShouldAllBe(span => span.EventCount == 0);
        server.TransientFailureCount.ShouldBe(1);
        spans.ShouldNotContain(span => span.ScopeName == "System.Net.Http");
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task AbortedReadAttempt_ExportsControlledFailureWithoutExceptionEvents()
    {
        await using var server = new AbortBeforeHeadersDiscoveryServer();
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        await using (var client = await CreateClientAsync(
                         receiver.Endpoint,
                         stderr,
                         baseUrl: server.BaseUrl,
                         calendarHrefs: server.CalendarHref))
        {
            var result = await client.CallToolAsync(
                "calendars.list",
                cancellationToken: TestContext.Current.CancellationToken);
            result.IsError.ShouldBe(false, result.StructuredContent?.ToString());
        }

        await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
        var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
        var discoveryPhases = spans.Where(span => span.Name == "caldav.phase.discovery").ToArray();
        var attempts = spans.Where(span =>
            span.ScopeName == "DotnetAgents.CalDav.Http"
            && Equals(span.Attributes.GetValueOrDefault("http.request.method"), "PROPFIND"))
            .ToArray();
        attempts.Length.ShouldBeGreaterThanOrEqualTo(2, string.Join(
            Environment.NewLine,
            spans.Select(span => $"{span.ScopeName} | {span.Name} | {string.Join(',', span.Attributes.Select(item => $"{item.Key}={item.Value}"))}")));
        attempts.Select(span => Convert.ToHexString(span.SpanId)).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(attempts.Length);
        attempts.ShouldContain(span =>
            Equals(span.Attributes.GetValueOrDefault("error.type"), "connection_error")
            || Equals(span.Attributes.GetValueOrDefault("error.type"), "response_ended"));
        attempts.ShouldContain(span =>
            Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 207L));
        attempts.ShouldAllBe(attempt => discoveryPhases.Any(phase =>
            attempt.ParentSpanId.SequenceEqual(phase.SpanId)));
        spans.ShouldNotContain(span => span.ScopeName == "System.Net.Http");
        spans.ShouldAllBe(span => span.EventCount == 0);
        server.AbortedRequestCount.ShouldBe(1);
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExhaustedReadRetries_KeepEveryAttemptAndOperationFailureTruthful()
    {
        await using var server = new RetryingDiscoveryServer(int.MaxValue);
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        await using (var client = await CreateClientAsync(
                         receiver.Endpoint,
                         stderr,
                         baseUrl: server.BaseUrl,
                         calendarHrefs: server.CalendarHref))
        {
            var result = await client.CallToolAsync(
                "calendars.list",
                cancellationToken: TestContext.Current.CancellationToken);
            result.IsError.ShouldBe(true, result.StructuredContent?.ToString());
        }

        await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
        var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
        var attempts = spans.Where(span =>
            span.ScopeName == "DotnetAgents.CalDav.Http"
            && Equals(span.Attributes.GetValueOrDefault("http.request.method"), "PROPFIND"))
            .ToArray();
        attempts.Length.ShouldBe(3);
        attempts.ShouldAllBe(attempt =>
            Equals(attempt.Attributes.GetValueOrDefault("http.response.status_code"), 503L)
            && attempt.StatusCode == 2);
        attempts.Select(attempt => attempt.Attributes.GetValueOrDefault("http.request.resend_count"))
            .ShouldBe([0L, 1L, 2L]);
        var operation = spans.Single(span =>
            span.Name == "caldav.operation"
            && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendars.list"));
        operation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("error");
        operation.Attributes.GetValueOrDefault("caldav.transport.recovered").ShouldBeNull();
        operation.Attributes.GetValueOrDefault("caldav.transport.retry_count").ShouldBe(2L);
        operation.StatusCode.ShouldBe(2);
        spans.ShouldAllBe(span => span.EventCount == 0);
        server.TransientFailureCount.ShouldBe(3);
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerCancellation_ExportsCooperativeCancellationOverStdio()
    {
        await using var server = new HangingDiscoveryServer();
        await using var receiver = OtlpLoopbackReceiver.Start();
        using var process = CreateRawTelemetryProcess(
            receiver.Endpoint,
            baseUrl: server.BaseUrl,
            calendarHrefs: server.CalendarHref);
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        try
        {
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"cancellation-test\",\"version\":\"1\"}}}");
            _ = await ReadRawResponseAsync(process, 1);
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"calendars.list\",\"arguments\":{}}}");
            await server.RequestStarted.WaitAsync(TestContext.Current.CancellationToken);
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":2,\"reason\":\"caller cancelled\"}}");
            var operation = await WaitForOperationSpanAsync(
                receiver,
                "calendars.list",
                TestContext.Current.CancellationToken);
            operation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("cancelled");
            operation.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBeNull();
            operation.Attributes.GetValueOrDefault("error.type").ShouldBeNull();
            operation.StatusCode.ShouldBe(0);
            OtlpProtobufReader.ReadSpans(receiver.Requests)
                .ShouldAllBe(span => span.EventCount == 0);
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            (await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
            (await stderrTask).ShouldBeEmpty();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task SemanticMoveCancellationBeforeDispatch_ExportsNotAttemptedTruthOverStdio()
    {
        await using var server = new HangingDiscoveryServer();
        await using var receiver = OtlpLoopbackReceiver.Start();
        using var process = CreateRawTelemetryProcess(
            receiver.Endpoint,
            baseUrl: server.BaseUrl,
            calendarHrefs: server.CalendarHref);
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        try
        {
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"move-cancellation-test\",\"version\":\"1\"}}}");
            _ = await ReadRawResponseAsync(process, 1);
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await WriteRawAsync(process, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = "calendar_resources.move",
                    arguments = MoveArguments(
                        $"{server.CalendarHref}private-source.ics",
                        "private-cancelled-move",
                        "\"private-revision\"",
                        server.CalendarHref)
                }
            }));
            await server.RequestStarted.WaitAsync(TestContext.Current.CancellationToken);
            await WriteRawAsync(process,
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":2,\"reason\":\"caller cancelled\"}}");
            var operation = await WaitForOperationSpanAsync(
                receiver,
                "calendar_resources.move",
                TestContext.Current.CancellationToken);
            operation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("cancelled");
            operation.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBe("not_attempted");
            operation.Attributes.GetValueOrDefault("caldav.move.dispatch").ShouldBe("not_attempted");
            operation.Attributes.GetValueOrDefault("caldav.move.collision").ShouldBeNull();
            operation.Attributes.GetValueOrDefault("caldav.move.reconciliation").ShouldBe("not_run");
            operation.Attributes.GetValueOrDefault("error.type").ShouldBeNull();
            operation.StatusCode.ShouldBe(0);
            OtlpProtobufReader.ReadSpans(receiver.Requests).ShouldAllBe(span => span.EventCount == 0);
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            (await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
            (await stderrTask).ShouldBeEmpty();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task CommittedCreateWithoutStrongRevision_ExportsControlledCommittedFailureOverStdio()
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using var server = new RetryingDiscoveryServer(
            failuresBeforeSuccess: 0,
            mutationWithoutEntityTag: true);
        await using var receiver = OtlpLoopbackReceiver.Start();
        var stderr = new ConcurrentQueue<string>();

        await using (var client = await CreateClientAsync(
                         receiver.Endpoint,
                         stderr,
                         baseUrl: server.BaseUrl,
                         calendarHrefs: server.CalendarHref))
        {
            var result = await client.CallToolAsync(
                "events.create",
                CreateEventArguments(
                    $"private-committed-{suffix}",
                    $"Private committed failure {suffix}",
                    server.CalendarHref),
                cancellationToken: TestContext.Current.CancellationToken);
            result.IsError.ShouldBe(true, result.StructuredContent?.ToString());
            var structured = result.StructuredContent!.Value;
            structured.GetProperty("code").GetString().ShouldBe("committed_but_concurrency_unavailable");
            structured.GetProperty("mutationState").GetString().ShouldBe("committed");
        }

        await receiver.WaitForPathsAsync(["/v1/traces"], TestContext.Current.CancellationToken);
        var spans = OtlpProtobufReader.ReadSpans(receiver.Requests);
        var operation = spans.Single(span =>
            span.Name == "caldav.operation"
            && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "events.create"));
        operation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("error");
        operation.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBe("committed");
        operation.Attributes.GetValueOrDefault("error.type")
            .ShouldBe("caldav.committed_but_concurrency_unavailable");
        operation.Attributes.GetValueOrDefault("caldav.error.category").ShouldBe("postWriteTruth");
        operation.Attributes.GetValueOrDefault("caldav.error.phase")
            .ShouldBe("postWriteVerificationOrReconciliation");
        operation.StatusCode.ShouldBe(2);
        spans.ShouldAllBe(span => span.EventCount == 0);
        server.PutCount.ShouldBe(1);
        stderr.ShouldBeEmpty();
    }

    private static void AssertTraceWaterfalls(IReadOnlyList<OtlpRequest> requests)
    {
        var spans = OtlpProtobufReader.ReadSpans(requests);
        AssertOccurrenceQueryWaterfall(spans);
        var createMcp = spans.Single(span =>
            span.ScopeName == "Experimental.ModelContextProtocol"
            && span.Name == "tools/call events.create");
        var createOperation = spans.Single(span =>
            span.ScopeName == "DotnetAgents.CalDav"
            && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "events.create"));
        createOperation.ParentSpanId.ShouldBe(createMcp.SpanId);
        createOperation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("success");
        createOperation.Attributes.GetValueOrDefault("caldav.mutation.state").ShouldBe("committed");
        createOperation.Attributes.GetValueOrDefault("error.type").ShouldBeNull();
        createOperation.StatusCode.ShouldBe(0);
        var createPhases = spans.Where(span => span.ParentSpanId.SequenceEqual(createOperation.SpanId)).ToArray();
        createPhases.ShouldContain(span => span.Name == "caldav.phase.reconcile");
        spans.ShouldContain(span =>
            span.ScopeName == "DotnetAgents.CalDav.Http"
            && createPhases.Any(phase => span.ParentSpanId.SequenceEqual(phase.SpanId))
            && span.Attributes.ContainsKey("http.request.method"));
        spans.ShouldAllBe(span => span.EventCount == 0);
        spans.Where(span => span.ScopeName == "DotnetAgents.CalDav")
            .ShouldAllBe(span =>
                span.Name == "caldav.operation"
                || span.Name.StartsWith("caldav.phase.", StringComparison.Ordinal)
                || span.Name.StartsWith("caldav.query.phase.", StringComparison.Ordinal));
        spans.SelectMany(span => span.Attributes.Keys).ShouldNotContain("url.full");
        spans.SelectMany(span => span.Attributes.Keys).ShouldNotContain("mcp.resource.uri");
    }

    private static void AssertOccurrenceQueryWaterfall(IReadOnlyList<OtlpSpan> spans)
    {
        var queryMcps = spans.Where(span =>
            span.ScopeName == "Experimental.ModelContextProtocol"
            && span.Name == "tools/call calendar_occurrences.query").ToArray();
        var queryOperations = spans.Where(span =>
            span.ScopeName == "DotnetAgents.CalDav"
            && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendar_occurrences.query"))
            .ToArray();
        queryMcps.Length.ShouldBe(2);
        queryOperations.Length.ShouldBe(2);
        var queryStart = queryOperations.Single(span =>
            Equals(span.Attributes.GetValueOrDefault("caldav.query.mode"), "start"));
        var queryContinue = queryOperations.Single(span =>
            Equals(span.Attributes.GetValueOrDefault("caldav.query.mode"), "continue"));
        queryMcps.ShouldContain(span => queryStart.ParentSpanId.SequenceEqual(span.SpanId));
        queryMcps.ShouldContain(span => queryContinue.ParentSpanId.SequenceEqual(span.SpanId));
        spans.Where(span => span.ParentSpanId.SequenceEqual(queryStart.SpanId)
                && span.Name.StartsWith("caldav.query.phase.", StringComparison.Ordinal))
            .Select(span => span.Attributes.GetValueOrDefault("caldav.query.phase"))
            .ShouldContain("evaluation");
        spans.Where(span => span.ParentSpanId.SequenceEqual(queryContinue.SpanId)
                && span.Name.StartsWith("caldav.query.phase.", StringComparison.Ordinal))
            .Select(span => span.Attributes.GetValueOrDefault("caldav.query.phase"))
            .ShouldBe(["snapshot_lookup", "page_admission"]);
        queryContinue.Attributes.Where(attribute =>
                attribute.Key.StartsWith("caldav.query.", StringComparison.Ordinal))
            .Select(attribute => attribute.Key)
            .Order(StringComparer.Ordinal)
            .ShouldBe([
                "caldav.query.mode",
                "caldav.query.page_admission_count",
                "caldav.query.snapshot_lookup_count"
            ]);
        spans.Where(span => span.TraceId.SequenceEqual(queryContinue.TraceId))
            .ShouldNotContain(span => span.ScopeName == "DotnetAgents.CalDav.Http");
    }

    private static void AssertLogsAndMetrics(IReadOnlyList<OtlpRequest> requests)
    {
        var metrics = OtlpProtobufReader.ReadMetrics(requests);
        metrics.ShouldContain(metric =>
            metric.ScopeName == "Experimental.ModelContextProtocol"
            && metric.Name == "mcp.server.operation.duration"
            && metric.Unit == "s");
        metrics.SelectMany(metric => metric.DataPointAttributes)
            .SelectMany(attributes => attributes)
            .ShouldAllBe(attribute => IsSafeMetricAttribute(attribute));
        var spans = OtlpProtobufReader.ReadSpans(requests);
        var logs = OtlpProtobufReader.ReadLogs(requests);
        logs.ShouldContain(log =>
            Equals(log.Body, "CalDAV diagnostic")
            && log.TraceId.Length == 16
            && log.SpanId.Length == 8
            && spans.Any(span => span.TraceId.SequenceEqual(log.TraceId)));
        logs.SelectMany(log => log.Attributes.Keys)
            .All(key => key is "Category" or "Code" or "MutationState" or "Outcome" or "Phase")
            .ShouldBeTrue();
    }

    private void AssertProhibitedDataAbsent(
        IReadOnlyList<OtlpRequest> requests,
        params string[] privateValues)
    {
        var prohibited = privateValues.Concat([
            _fixture.BaseUrl,
            "caldavtest",
            "caldavtest123",
            "otlp-private-header",
            "BEGIN:VCALENDAR",
            "Authorization",
            "If-Match",
            "Overwrite",
            "Destination",
            "structuredContent",
            "exception.message",
            "exception.stacktrace"
        ]);
        prohibited.ShouldAllBe(value => !OtlpProtobufReader.ContainsUtf8(requests, value));
    }

    private static bool IsSafeMetricAttribute(KeyValuePair<string, object?> attribute) =>
        attribute.Key switch
        {
            "rpc.response.status_code" => attribute.Value is long,
            "mcp.protocol.version" => Equals(attribute.Value, "2026-07-28"),
            "network.protocol.name" => Equals(attribute.Value, "mcp"),
            "network.transport" => Equals(attribute.Value, "pipe"),
            _ => false
        };

    private static async Task<OtlpSpan> WaitForOperationSpanAsync(
        OtlpLoopbackReceiver receiver,
        string toolName,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var operation = OtlpProtobufReader.ReadSpans(receiver.Requests).SingleOrDefault(span =>
                span.Name == "caldav.operation"
                && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), toolName));
            if (operation is not null)
                return operation;
            if (!await receiver.WaitForRequestAsync(cancellationToken))
                throw new TimeoutException($"Timed out waiting for {toolName} Operation telemetry.");
        }
    }

    private static async Task<OtlpSpan[]> WaitForQueryOperationsAsync(
        OtlpLoopbackReceiver receiver,
        int expectedCount)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(15);
        do
        {
            var operations = OtlpProtobufReader.ReadSpans(receiver.Requests).Where(span =>
                span.Name == "caldav.operation"
                && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendar_entities.query"))
                .ToArray();
            if (operations.Length == expectedCount)
                return operations;
            if (!await receiver.WaitForRequestAsync(TestContext.Current.CancellationToken))
                return operations;
        } while (TimeProvider.System.GetUtcNow() < deadline);
        return OtlpProtobufReader.ReadSpans(receiver.Requests).Where(span =>
            span.Name == "caldav.operation"
            && Equals(span.Attributes.GetValueOrDefault("caldav.tool.name"), "calendar_entities.query"))
            .ToArray();
    }

    private static Dictionary<string, object?> StartEntityQueryArguments(
        string? selectedCalendarHref = null,
        int pageSize = 10)
    {
        var scope = selectedCalendarHref is null
            ? new Dictionary<string, object?> { ["mode"] = "all" }
            : new Dictionary<string, object?>
            {
                ["mode"] = "selected",
                ["calendar"] = new Dictionary<string, object?>
                {
                    ["by"] = "href",
                    ["href"] = selectedCalendarHref
                }
            };
        return new Dictionary<string, object?>
        {
            ["scope"] = scope,
            ["entityKinds"] = new[] { "event" },
            ["pageSize"] = pageSize
        };
    }

    private static void AssertFallbackOperation(OtlpSpan operation, long? multigetResourceCount)
    {
        operation.StatusCode.ShouldBe(0);
        operation.Attributes.GetValueOrDefault("caldav.outcome").ShouldBe("success");
        operation.Attributes.GetValueOrDefault("caldav.query.fallback_reason")
            .ShouldBe("multiget_unavailable");
        operation.Attributes.GetValueOrDefault("caldav.query.direct_get_resource_count").ShouldBe(2L);
        operation.Attributes.GetValueOrDefault("caldav.query.direct_get_attempt_count").ShouldBe(2L);
        operation.Attributes.GetValueOrDefault("caldav.query.disappeared_resource_count").ShouldBe(1L);
        operation.Attributes.GetValueOrDefault("caldav.query.multiget_resource_count")
            .ShouldBe(multigetResourceCount ?? 0L);
    }

    private static void AssertDirectReadTrace(IReadOnlyCollection<OtlpSpan> spans)
    {
        var directReads = spans.Where(span => span.ScopeName == "DotnetAgents.CalDav.Http"
            && span.Name == "GET"
            && Equals(span.Attributes.GetValueOrDefault("caldav.http.request_purpose"), "query_resource_read"))
            .ToArray();
        directReads.Length.ShouldBe(2);
        directReads.ShouldContain(span =>
            Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 200L)
            && span.StatusCode == 0);
        directReads.ShouldContain(span =>
            Equals(span.Attributes.GetValueOrDefault("http.response.status_code"), 404L)
            && Equals(span.Attributes.GetValueOrDefault("caldav.http.observation"), "resource_disappeared")
            && span.StatusCode == 1
            && !span.Attributes.ContainsKey("error.type"));
    }

    private async Task<McpClient> CreateClientAsync(
        Uri endpoint,
        ConcurrentQueue<string> stderr,
        string? baseUrl = null,
        string? calendarHrefs = null,
        string? defaultTodoCalendarName = null,
        string? evaluationTimeZone = null,
        bool exposeExact = false,
        bool confirmMutations = false)
    {
        baseUrl ??= _fixture.BaseUrl;
        var eventCalendarHref = $"{baseUrl}{_fixture.EventCalendarHref}";
        var todoCalendarHref = $"{baseUrl}{_fixture.TodoCalendarHref}";
        calendarHrefs ??= $"{eventCalendarHref},{todoCalendarHref}";
        var launch = McpStdioClientFactory.CreateBuiltServerLaunch(
            new Dictionary<string, string?>
        {
            ["CALDAV_URL"] = baseUrl,
            ["CALDAV_USERNAME"] = "caldavtest",
            ["CALDAV_PASSWORD"] = "caldavtest123",
            ["CALDAV_INTEROPERABILITY_PROFILE"] = "radicale-3.7.8",
            ["CALDAV_CALENDAR_HREFS"] = calendarHrefs,
            ["CALDAV_DEFAULT_TODO_CALENDAR_NAME"] = defaultTodoCalendarName,
            ["CALDAV_EVALUATION_TIME_ZONE"] = evaluationTimeZone,
            ["CALDAV_EXPOSE_EXACT_TOOLS"] = exposeExact ? "true" : "false",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "authorization=Bearer otlp-private-header",
            ["OTEL_SERVICE_NAME"] = "dotnet-agents-caldav-test",
            ["OTEL_SDK_DISABLED"] = "false",
            ["OTEL_BSP_SCHEDULE_DELAY"] = "100",
            ["OTEL_BLRP_SCHEDULE_DELAY"] = "100",
            ["OTEL_METRIC_EXPORT_INTERVAL"] = "100"
        }, stderr.Enqueue);
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
        return await McpStdioClientFactory.ConnectAsync(
            launch,
            options,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private Dictionary<string, object?> OccurrenceQueryArguments() => new()
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
            ["value"] = "2026-08-15T09:00:00Z"
        },
        ["to"] = new Dictionary<string, object?>
        {
            ["kind"] = "utcDateTime",
            ["value"] = "2026-08-18T11:00:00Z"
        },
        ["evaluationTimeZone"] = "UTC",
        ["pageSize"] = 1
    };

    private Dictionary<string, object?> CreateEventArguments(
        string uid,
        string summary,
        string? calendarHref = null) => new()
    {
        ["destination"] = new Dictionary<string, object?>
        {
            ["mode"] = "selected",
            ["calendar"] = new Dictionary<string, object?>
            {
                ["by"] = "href",
                ["href"] = calendarHref ?? $"{_fixture.BaseUrl}{_fixture.EventCalendarHref}"
            }
        },
        ["entity"] = new Dictionary<string, object?>
        {
            ["kind"] = "event",
            ["uid"] = uid,
            ["fields"] = new Dictionary<string, object?>
            {
                ["summary"] = summary,
                ["start"] = new Dictionary<string, object?>
                {
                    ["kind"] = "utcDateTime",
                    ["value"] = "2026-08-21T13:00:00Z"
                },
                ["duration"] = "PT1H"
            }
        }
    };

    private static Dictionary<string, object?> MoveArguments(
        string sourceHref,
        string uid,
        string entityTag,
        string destinationCalendarHref) => new()
    {
        ["revision"] = new Dictionary<string, object?>
        {
            ["href"] = sourceHref,
            ["entityUid"] = uid,
            ["entityKind"] = "todo",
            ["entityTag"] = entityTag
        },
        ["destination"] = new Dictionary<string, object?>
        {
            ["mode"] = "selected",
            ["calendar"] = new Dictionary<string, object?>
            {
                ["by"] = "href",
                ["href"] = destinationCalendarHref
            }
        }
    };

    private static Dictionary<string, object?> ExactMoveArguments(
        string sourceHref,
        string uid,
        string entityTag,
        string destinationHref) => new()
    {
        ["revision"] = new Dictionary<string, object?>
        {
            ["href"] = sourceHref,
            ["entityUid"] = uid,
            ["entityKind"] = "todo",
            ["entityTag"] = entityTag
        },
        ["destinationHref"] = destinationHref
    };

    private static string RecurringTodo(string uid) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Telemetry Test//EN\r\n"
        + $"BEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260821T120000Z\r\n"
        + "DTSTART:20260815T100000Z\r\nDUE:20260815T103000Z\r\n"
        + "RRULE:FREQ=DAILY;COUNT=3\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private static string PrivateTodo(string uid) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Telemetry Privacy Test//EN\r\n"
        + $"BEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260821T120000Z\r\n"
        + "SUMMARY:Private reviewed MRTR resource\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private static string SnapshotTodo(string uid, string summary) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Query Snapshot Telemetry//EN\r\n"
        + $"BEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260823T120000Z\r\n"
        + $"SUMMARY:{summary}\r\nDUE;VALUE=DATE:20260824\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    private static string ExactEvent(string uid, string summary) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Telemetry Exact Review//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260823T120000Z\r\n"
        + $"SUMMARY:{summary}\r\nDTSTART:20260824T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private async Task<string> PutResourceAsync(string calendarPath, string name, string content)
    {
        var href = $"{_fixture.BaseUrl}{calendarPath}{name}";
        using var client = CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, href)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/calendar")
        };
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return href;
    }

    private static async Task DeleteResourceAsync(string href, string? entityTag)
    {
        using var client = CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, href);
        if (entityTag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NotFound);
    }

    private static async Task<string> GetEntityTagAsync(string href)
    {
        using var client = CreateAuthenticatedClient();
        using var response = await client.GetAsync(href, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag?.Tag
            ?? throw new InvalidDataException("Radicale response did not contain a strong Entity Tag.");
    }

    private static HttpClient CreateAuthenticatedClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("caldavtest:caldavtest123")));
        return client;
    }

    private Process CreateRawTelemetryProcess(
        Uri endpoint,
        bool exposeExact = false,
        string? baseUrl = null,
        string? calendarHrefs = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = GetServerAssemblyPath(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        var environment = process.StartInfo.Environment;
        baseUrl ??= _fixture.BaseUrl;
        calendarHrefs ??=
            $"{_fixture.BaseUrl}{_fixture.EventCalendarHref},{_fixture.BaseUrl}{_fixture.TodoCalendarHref}";
        environment["CALDAV_URL"] = baseUrl;
        environment["CALDAV_USERNAME"] = "caldavtest";
        environment["CALDAV_PASSWORD"] = "caldavtest123";
        environment["CALDAV_INTEROPERABILITY_PROFILE"] = "radicale-3.7.8";
        environment["CALDAV_CALENDAR_HREFS"] = calendarHrefs;
        environment["CALDAV_EXPOSE_EXACT_TOOLS"] = exposeExact ? "true" : "false";
        environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint.GetLeftPart(UriPartial.Authority);
        environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        environment["OTEL_BSP_SCHEDULE_DELAY"] = "50";
        environment["OTEL_BLRP_SCHEDULE_DELAY"] = "50";
        environment["OTEL_METRIC_EXPORT_INTERVAL"] = "50";
        return process;
    }

    private static async Task WriteRawAsync(Process process, string message)
    {
        await process.StandardInput.WriteLineAsync(message);
        await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadRawResponseAsync(Process process, int expectedId)
    {
        while (await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.TryGetProperty("id", out var id) && id.GetInt32() == expectedId)
                return message.Clone();
        }
        throw new EndOfStreamException($"MCP process ended before response {expectedId}.");
    }

    private sealed class QueryCompatibilityServer : IAsyncDisposable
    {
        private const string MultigetUid = "private-multiget-event";
        private const string DirectUid = "private-direct-event";
        private const string PrivateSummary = "private compatibility event";
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serve;
        private int _directGetCount;
        private int _multigetReportCount;

        internal QueryCompatibilityServer()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/";
            MultigetCalendarHref = $"{BaseUrl}calendars/test/multiget/";
            DirectCalendarHref = $"{BaseUrl}calendars/test/direct/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serve = ServeAsync();
        }

        internal string BaseUrl { get; }

        internal string DirectCalendarHref { get; }

        internal int DirectGetCount => Volatile.Read(ref _directGetCount);

        internal string MultigetCalendarHref { get; }

        internal int MultigetReportCount => Volatile.Read(ref _multigetReportCount);

        internal string[] PrivateValues =>
        [
            MultigetUid,
            DirectUid,
            PrivateSummary,
            MultigetCalendarHref,
            DirectCalendarHref
        ];

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                System.Diagnostics.Debug.Assert(_stopping.IsCancellationRequested);
            }
            _listener.Close();
            _stopping.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync()
                    .WaitAsync(_stopping.Token)
                    .ConfigureAwait(false);
                await RespondAsync(context).ConfigureAwait(false);
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            if (context.Request.HttpMethod == "PROPFIND")
            {
                await WriteXmlAsync(context.Response, path == "/"
                    ? $"<d:response><d:href>{BaseUrl}</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>{BaseUrl}calendars/test/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                    : $"<d:response><d:href>{MultigetCalendarHref}</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><d:displayname>Multiget</d:displayname><c:supported-calendar-component-set><c:comp name=\"VEVENT\"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response><d:response><d:href>{DirectCalendarHref}</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><d:displayname>Direct</d:displayname><c:supported-calendar-component-set><c:comp name=\"VEVENT\"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>")
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "REPORT")
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var requestBody = await reader.ReadToEndAsync(_stopping.Token).ConfigureAwait(false);
                if (requestBody.Contains("calendar-query", StringComparison.Ordinal))
                {
                    var responses = path == "/calendars/test/multiget/"
                        ? QueryCandidate($"{MultigetCalendarHref}event.ics")
                        : QueryCandidate($"{DirectCalendarHref}event.ics")
                          + QueryCandidate($"{DirectCalendarHref}missing.ics");
                    await WriteXmlAsync(context.Response, responses).ConfigureAwait(false);
                    return;
                }

                Interlocked.Increment(ref _multigetReportCount);
                if (path == "/calendars/test/direct/")
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotImplemented;
                    context.Response.Close();
                    return;
                }
                await WriteXmlAsync(
                    context.Response,
                    MultigetResource($"{MultigetCalendarHref}event.ics", MultigetUid, "multiget-revision"))
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path.StartsWith("/calendars/test/direct/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _directGetCount);
                if (path.EndsWith("missing.ics", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                    return;
                }
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.Headers["ETag"] = "\"direct-revision\"";
                await WriteBodyAsync(context.Response, EventResource(DirectUid), "text/calendar; charset=utf-8")
                    .ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            context.Response.Close();
        }

        private static string QueryCandidate(string href) =>
            $"<d:response><d:href>{href}</d:href><d:status>HTTP/1.1 200 OK</d:status></d:response>";

        private static string MultigetResource(string href, string uid, string entityTag) =>
            $"<d:response><d:href>{href}</d:href><d:propstat><d:prop><d:getetag>&quot;{entityTag}&quot;</d:getetag><c:calendar-data>{WebUtility.HtmlEncode(EventResource(uid))}</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

        private static string EventResource(string uid) =>
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Compatibility Witness//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260823T120000Z\r\nDTSTART:20260824T120000Z\r\nSUMMARY:{PrivateSummary}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        private static Task WriteXmlAsync(HttpListenerResponse response, string responses) =>
            WriteBodyAsync(
                response,
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">{responses}</d:multistatus>",
                "application/xml; charset=utf-8",
                HttpStatusCode.MultiStatus);

        private static async Task WriteBodyAsync(
            HttpListenerResponse response,
            string body,
            string contentType,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = (int)statusCode;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            response.Close();
        }
    }

    private sealed class PossiblyDispatchedMoveServer : IAsyncDisposable
    {
        internal const string PrivateUid = "private-possibly-dispatched-move";
        internal const string SourceEntityTag = "\"source-revision\"";
        internal const string PrivateContent = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Private Move//EN\r\n"
            + "BEGIN:VTODO\r\nUID:private-possibly-dispatched-move\r\nDTSTAMP:20260823T120000Z\r\n"
            + "SUMMARY:private possibly dispatched payload\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serve;
        private int _moved;
        private int _getCount;
        private int _moveCount;
        private int _reportCount;

        internal PossiblyDispatchedMoveServer()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/";
            SourceCalendarHref = $"{BaseUrl}calendars/test/source/";
            DestinationCalendarHref = $"{BaseUrl}calendars/test/destination/";
            SourceHref = $"{SourceCalendarHref}private-source.ics";
            DestinationHref = $"{DestinationCalendarHref}{OpaqueName(PrivateUid)}.ics";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serve = ServeAsync();
        }

        internal string BaseUrl { get; }

        internal string SourceCalendarHref { get; }

        internal string DestinationCalendarHref { get; }

        internal string SourceHref { get; }

        internal string DestinationHref { get; }

        internal int MoveCount => Volatile.Read(ref _moveCount);

        internal int GetCount => Volatile.Read(ref _getCount);

        internal int ReportCount => Volatile.Read(ref _reportCount);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                System.Diagnostics.Debug.Assert(_stopping.IsCancellationRequested);
            }
            _listener.Close();
            _stopping.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync()
                    .WaitAsync(_stopping.Token)
                    .ConfigureAwait(false);
                await RespondAsync(context).ConfigureAwait(false);
            }
        }

        private Task RespondAsync(HttpListenerContext context) => context.Request.HttpMethod switch
        {
            "PROPFIND" => RespondDiscoveryAsync(context),
            "GET" => RespondGetAsync(context),
            "MOVE" => RespondMoveAsync(context),
            "REPORT" => RespondReportAsync(context),
            _ => CompleteAsync(context, HttpStatusCode.MethodNotAllowed)
        };

        private static Task RespondDiscoveryAsync(HttpListenerContext context)
        {
            var responses = context.Request.Url!.AbsolutePath == "/"
                ? "<d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set>"
                    + "<d:href>/calendars/test/</d:href></c:calendar-home-set></d:prop>"
                    + "<d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                : CalendarResponse("/calendars/test/source/", "Private Source")
                    + CalendarResponse("/calendars/test/destination/", "Private Destination");
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus "
                + "xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
                + responses
                + "</d:multistatus>";
            return CompleteAsync(context, HttpStatusCode.MultiStatus, xml, "application/xml; charset=utf-8");
        }

        private Task RespondGetAsync(HttpListenerContext context)
        {
            Interlocked.Increment(ref _getCount);
            var href = context.Request.Url!.AbsoluteUri;
            var moved = Volatile.Read(ref _moved) == 1;
            if ((!moved && href == SourceHref) || (moved && href == DestinationHref))
            {
                var entityTag = moved ? "\"destination-revision\"" : SourceEntityTag;
                return CompleteAsync(context, HttpStatusCode.OK, PrivateContent, "text/calendar; charset=utf-8", entityTag);
            }
            return CompleteAsync(context, HttpStatusCode.NotFound);
        }

        private Task RespondMoveAsync(HttpListenerContext context)
        {
            Interlocked.Increment(ref _moveCount);
            var valid = context.Request.Url!.AbsoluteUri == SourceHref
                && context.Request.Headers["Destination"] == DestinationHref
                && context.Request.Headers["Overwrite"] == "F"
                && context.Request.Headers["If-Match"] == SourceEntityTag;
            if (valid)
                Volatile.Write(ref _moved, 1);
            return CompleteAsync(context, valid ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest);
        }

        private Task RespondReportAsync(HttpListenerContext context)
        {
            Interlocked.Increment(ref _reportCount);
            return CompleteAsync(context, HttpStatusCode.MethodNotAllowed);
        }

        private static async Task CompleteAsync(
            HttpListenerContext context,
            HttpStatusCode status,
            string? body = null,
            string? contentType = null,
            string? entityTag = null)
        {
            var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            if (contentType is not null)
                context.Response.ContentType = contentType;
            if (entityTag is not null)
                context.Response.Headers["ETag"] = entityTag;
            context.Response.ContentLength64 = bytes.Length;
            if (bytes.Length > 0)
                await context.Response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
            context.Response.Close();
        }

        private static string CalendarResponse(string href, string displayName) =>
            $"<d:response><d:href>{href}</d:href><d:propstat><d:prop>"
            + "<d:resourcetype><c:calendar/></d:resourcetype>"
            + $"<d:displayname>{displayName}</d:displayname>"
            + "<c:supported-calendar-component-set><c:comp name=\"VTODO\"/>"
            + "</c:supported-calendar-component-set></d:prop>"
            + "<d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

        private static string OpaqueName(string uid) =>
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(uid)))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
    }

    private sealed class RetryingDiscoveryServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serve;
        private readonly int _failuresBeforeSuccess;
        private readonly bool _mutationWithoutEntityTag;
        private byte[]? _resource;
        private int _putCount;
        private int _transientFailureCount;
        private int _propFindCount;

        internal RetryingDiscoveryServer(
            int failuresBeforeSuccess = 1,
            bool mutationWithoutEntityTag = false)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
            _mutationWithoutEntityTag = mutationWithoutEntityTag;
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/";
            CalendarHref = $"{BaseUrl}calendars/test/entities/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serve = ServeAsync();
        }

        internal string BaseUrl { get; }

        internal string CalendarHref { get; }

        internal int TransientFailureCount => Volatile.Read(ref _transientFailureCount);

        internal int PutCount => Volatile.Read(ref _putCount);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                System.Diagnostics.Debug.Assert(_stopping.IsCancellationRequested);
            }
            _listener.Close();
            _stopping.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync()
                    .WaitAsync(_stopping.Token)
                    .ConfigureAwait(false);
                await RespondAsync(context).ConfigureAwait(false);
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            if (_mutationWithoutEntityTag && context.Request.HttpMethod == "PUT")
            {
                using var content = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(content, _stopping.Token);
                Volatile.Write(ref _resource, content.ToArray());
                Interlocked.Increment(ref _putCount);
                context.Response.StatusCode = (int)HttpStatusCode.Created;
                context.Response.Close();
                return;
            }

            if (_mutationWithoutEntityTag && context.Request.HttpMethod == "GET")
            {
                var resource = Volatile.Read(ref _resource);
                if (resource is null)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                    return;
                }
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "text/calendar; charset=utf-8";
                context.Response.ContentLength64 = resource.Length;
                await context.Response.OutputStream.WriteAsync(resource, _stopping.Token);
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod != "PROPFIND")
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            if (Interlocked.Increment(ref _propFindCount) <= _failuresBeforeSuccess)
            {
                Interlocked.Increment(ref _transientFailureCount);
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                context.Response.Headers["Retry-After"] = "0";
                context.Response.Close();
                return;
            }

            var body = context.Request.Url!.AbsolutePath == "/"
                ? "<d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>/calendars/test/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                : "<d:response><d:href>/calendars/test/entities/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><d:displayname>Retry Calendar</d:displayname><c:supported-calendar-component-set><c:comp name=\"VEVENT\"/><c:comp name=\"VTODO\"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
                + body
                + "</d:multistatus>";
            var bytes = Encoding.UTF8.GetBytes(xml);
            context.Response.StatusCode = (int)HttpStatusCode.MultiStatus;
            context.Response.ContentType = "application/xml; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
            context.Response.Close();
        }
    }

    private sealed class AbortBeforeHeadersDiscoveryServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serve;
        private int _abortedRequestCount;
        private int _requestCount;

        internal AbortBeforeHeadersDiscoveryServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/";
            CalendarHref = $"{BaseUrl}calendars/test/entities/";
            _serve = ServeAsync();
        }

        internal string BaseUrl { get; }

        internal string CalendarHref { get; }

        internal int AbortedRequestCount => Volatile.Read(ref _abortedRequestCount);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                System.Diagnostics.Debug.Assert(_stopping.IsCancellationRequested);
            }
            _stopping.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stopping.Token)
                    .ConfigureAwait(false);
                await RespondAsync(client).ConfigureAwait(false);
            }
        }

        private async Task RespondAsync(TcpClient client)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(_stopping.Token);
            while (await reader.ReadLineAsync(_stopping.Token) is { Length: > 0 })
            {
            }

            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                Interlocked.Increment(ref _abortedRequestCount);
                client.Client.LingerState = new LingerOption(enable: true, seconds: 0);
                return;
            }

            var path = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ElementAtOrDefault(1);
            var body = path == "/"
                ? "<d:response><d:href>/</d:href><d:propstat><d:prop><c:calendar-home-set><d:href>/calendars/test/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                : "<d:response><d:href>/calendars/test/entities/</d:href><d:propstat><d:prop><d:resourcetype><c:calendar/></d:resourcetype><d:displayname>Abort Recovery Calendar</d:displayname><c:supported-calendar-component-set><c:comp name=\"VEVENT\"/><c:comp name=\"VTODO\"/></c:supported-calendar-component-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
            var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">"
                + body
                + "</d:multistatus>";
            var bytes = Encoding.UTF8.GetBytes(xml);
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 207 Multi-Status\r\n"
                + "Content-Type: application/xml; charset=utf-8\r\n"
                + $"Content-Length: {bytes.Length}\r\n"
                + "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, _stopping.Token);
            await stream.WriteAsync(bytes, _stopping.Token);
        }
    }

    private sealed class HangingDiscoveryServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serve;
        private readonly TaskCompletionSource _requestStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseRequest = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal HangingDiscoveryServer()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/";
            CalendarHref = $"{BaseUrl}calendars/test/entities/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _serve = ServeAsync();
        }

        internal string BaseUrl { get; }

        internal string CalendarHref { get; }

        internal Task RequestStarted => _requestStarted.Task;

        public async ValueTask DisposeAsync()
        {
            _releaseRequest.TrySetResult();
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _serve;
            }
            catch (Exception exception) when (_stopping.IsCancellationRequested
                && exception is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                System.Diagnostics.Debug.Assert(_stopping.IsCancellationRequested);
            }
            _listener.Close();
            _stopping.Dispose();
        }

        private async Task ServeAsync()
        {
            var context = await _listener.GetContextAsync()
                .WaitAsync(_stopping.Token)
                .ConfigureAwait(false);
            _requestStarted.TrySetResult();
            await _releaseRequest.Task.WaitAsync(_stopping.Token).ConfigureAwait(false);
            context.Response.Close();
        }
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
