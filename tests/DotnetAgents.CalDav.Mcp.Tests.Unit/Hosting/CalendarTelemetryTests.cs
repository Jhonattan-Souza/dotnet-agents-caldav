using System.Diagnostics;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using DotnetAgents.CalDav.Mcp.Hosting;
using OpenTelemetry.Logs;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit.Hosting;

[Collection("TelemetryActivityCollection")]
public sealed class CalendarTelemetryTests
{
    [Fact]
    public void Operation_EmitsStableParentedPhaseWaterfallWithSafeDimensions()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        using var mcpActivity = new Activity("tools/call").Start();

        using (var operation = CalendarTelemetry.StartOperation(
                   "calendar_occurrences.query",
                   CalendarTelemetryEntityKind.Event))
        {
            operation.ShouldNotBeNull();
            operation.StartPhase(CalendarOperationPhase.Discovery);
            operation.StartPhase(CalendarOperationPhase.Fetch);
            operation.StartPhase(CalendarOperationPhase.Filter);
            operation.StartPhase(CalendarOperationPhase.Expand);
            operation.Complete(outcome: "success");
        }

        var operationActivity = stopped.Single(activity => activity.OperationName == "caldav.operation");
        operationActivity.Source.Name.ShouldBe("DotnetAgents.CalDav");
        operationActivity.Source.Version.ShouldBe("0.1.0");
        operationActivity.ParentId.ShouldBe(mcpActivity.Id);
        operationActivity.GetTagItem("caldav.tool.name").ShouldBe("calendar_occurrences.query");
        operationActivity.GetTagItem("caldav.entity.kind").ShouldBe("event");
        operationActivity.GetTagItem("caldav.outcome").ShouldBe("success");

        stopped.Where(activity => activity.OperationName.StartsWith("caldav.phase.", StringComparison.Ordinal))
            .OrderBy(activity => activity.StartTimeUtc)
            .Select(activity => (activity.OperationName, ParentId: activity.ParentId, Phase: activity.GetTagItem("caldav.phase")))
            .ShouldBe([
                ("caldav.phase.discovery", operationActivity.Id, (object?)"discovery"),
                ("caldav.phase.fetch", operationActivity.Id, (object?)"fetch"),
                ("caldav.phase.filter", operationActivity.Id, (object?)"filter"),
                ("caldav.phase.expand", operationActivity.Id, (object?)"expand")
            ]);
    }

    [Theory]
    [InlineData("success", null, ActivityStatusCode.Unset)]
    [InlineData("success", "committed", ActivityStatusCode.Unset)]
    [InlineData("success", "not_attempted", ActivityStatusCode.Unset)]
    [InlineData("input_required", "not_attempted", ActivityStatusCode.Unset)]
    [InlineData("cancelled", null, ActivityStatusCode.Unset)]
    [InlineData("error", "not_committed", ActivityStatusCode.Error)]
    [InlineData("error", "committed", ActivityStatusCode.Error)]
    [InlineData("error", "unknown", ActivityStatusCode.Error)]
    public void Operation_EmitsClosedOutcomeAndIndependentMutationStateMatrix(
        string outcome,
        string? mutationState,
        ActivityStatusCode expectedStatus)
    {
        Activity? stoppedOperation = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedOperation = activity
        };
        ActivitySource.AddActivityListener(listener);

        using (var operation = CalendarTelemetry.StartOperation("todos.complete", null))
        {
            operation.ShouldNotBeNull();
            operation.Complete(outcome, mutationState: mutationState);
        }

        stoppedOperation.ShouldNotBeNull();
        stoppedOperation.GetTagItem("caldav.outcome").ShouldBe(outcome);
        stoppedOperation.GetTagItem("caldav.mutation.state").ShouldBe(mutationState);
        stoppedOperation.Status.ShouldBe(expectedStatus);
    }

    [Fact]
    public void Operation_StructuredCommittedFailureExportsOnlyControlledFailureDimensions()
    {
        Activity? stoppedOperation = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedOperation = activity
        };
        ActivitySource.AddActivityListener(listener);

        using (var operation = CalendarTelemetry.StartOperation("events.patch", null))
        {
            operation.ShouldNotBeNull();
            operation.Complete(
                "error",
                "fidelity_failure",
                "postWriteTruth",
                "committed",
                "postWriteVerificationOrReconciliation",
                retryable: false);
        }

        stoppedOperation.ShouldNotBeNull();
        stoppedOperation.GetTagItem("caldav.outcome").ShouldBe("error");
        stoppedOperation.GetTagItem("caldav.error.code").ShouldBe("fidelity_failure");
        stoppedOperation.GetTagItem("caldav.error.category").ShouldBe("postWriteTruth");
        stoppedOperation.GetTagItem("caldav.error.phase").ShouldBe("postWriteVerificationOrReconciliation");
        stoppedOperation.GetTagItem("caldav.error.retryable").ShouldBe(false);
        stoppedOperation.GetTagItem("caldav.mutation.state").ShouldBe("committed");
        stoppedOperation.GetTagItem("error.type").ShouldBe("caldav.fidelity_failure");
        stoppedOperation.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public void StartOperation_WithoutListeners_ReturnsNull()
    {
        CalendarTelemetry.StartOperation(
            "todos.complete",
            CalendarTelemetryEntityKind.Todo).ShouldBeNull();
    }

    [Fact]
    public void ExportAllowlist_RemovesIdentifiersPayloadsUrlsAndExceptionDetails()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = static _ => true,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("Experimental.ModelContextProtocol");
        using var activity = source.StartActivity("resources/read https://calendar.example/private/user.ics");
        activity.ShouldNotBeNull();
        activity.SetTag("mcp.method.name", "resources/read");
        activity.SetTag("mcp.resource.uri", "caldav://snapshot/private-user-resource");
        activity.SetTag("url.full", "https://calendar.example/private/user.ics?token=secret");
        activity.SetTag("calendar.uid", "private-uid");
        activity.SetTag("exception.message", "secret body");
        activity.SetTag("error.type", "System.Net.Http.HttpRequestException");
        activity.TraceStateString = "private=trace-state-secret";
        activity.SetStatus(ActivityStatusCode.Error, "secret tool result");
        activity.Stop();

        var processor = new TelemetryActivityAllowlistProcessor();
        processor.OnStart(activity);
        processor.OnEnd(activity);

        activity.DisplayName.ShouldBe("resources/read");
        activity.GetTagItem("mcp.method.name").ShouldBe("resources/read");
        activity.GetTagItem("error.type").ShouldBeNull();
        activity.GetTagItem("mcp.resource.uri").ShouldBeNull();
        activity.GetTagItem("url.full").ShouldBeNull();
        activity.GetTagItem("calendar.uid").ShouldBeNull();
        activity.GetTagItem("exception.message").ShouldBeNull();
        activity.TraceStateString.ShouldBeNull();
        activity.StatusDescription.ShouldBeNull();
    }

    [Fact]
    public void ExportAllowlist_NormalizesMcpToolAndProtocolDimensions()
    {
        using var listener = ListenTo(OpenTelemetryHostConfiguration.McpInstrumentationName);
        using var source = new ActivitySource(OpenTelemetryHostConfiguration.McpInstrumentationName);
        using var activity = source.StartActivity("unsafe tool request");
        activity.ShouldNotBeNull();
        activity.SetTag("mcp.method.name", "tools/call");
        activity.SetTag("gen_ai.tool.name", "todos.complete");
        activity.SetTag("mcp.protocol.version", "2025-06-18");
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.DisplayName.ShouldBe("tools/call todos.complete");
        activity.GetTagItem("mcp.method.name").ShouldBe("tools/call");
        activity.GetTagItem("gen_ai.tool.name").ShouldBe("todos.complete");
        activity.GetTagItem("mcp.protocol.version").ShouldBe("2025-06-18");
    }

    [Fact]
    public void ExportAllowlist_ReplacesUnknownMcpDimensionsAndInvalidText()
    {
        using var listener = ListenTo(OpenTelemetryHostConfiguration.McpInstrumentationName);
        using var source = new ActivitySource(OpenTelemetryHostConfiguration.McpInstrumentationName);
        using var activity = source.StartActivity("unsafe request");
        activity.ShouldNotBeNull();
        activity.SetTag("mcp.method.name", "private/method");
        activity.SetTag("gen_ai.tool.name", "private.tool");
        activity.SetTag("mcp.protocol.version", "secret/version");
        activity.SetTag("error.type", "secret exception message!");
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.DisplayName.ShouldBe("mcp.request");
        activity.GetTagItem("mcp.method.name").ShouldBe("mcp.request");
        activity.GetTagItem("gen_ai.tool.name").ShouldBeNull();
        activity.GetTagItem("mcp.protocol.version").ShouldBeNull();
        activity.GetTagItem("error.type").ShouldBeNull();
    }

    [Fact]
    public void ExportAllowlist_RemovesPrivateLookingValuesEvenWhenLexicallyValid()
    {
        using var listener = ListenTo(CalendarTelemetry.InstrumentationName);
        using var source = new ActivitySource(CalendarTelemetry.InstrumentationName);
        using var activity = source.StartActivity("caldav.operation");
        activity.ShouldNotBeNull();
        activity.SetTag("caldav.outcome", "private_success");
        activity.SetTag("caldav.error.code", "private_secret_code");
        activity.SetTag("caldav.error.category", "privateCategory");
        activity.SetTag("caldav.error.phase", "privatePhase");
        activity.SetTag("caldav.mutation.state", "private_state");
        activity.SetTag("error.type", "caldav.private_secret_code");
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.GetTagItem("caldav.outcome").ShouldBeNull();
        activity.GetTagItem("caldav.error.code").ShouldBeNull();
        activity.GetTagItem("caldav.error.category").ShouldBeNull();
        activity.GetTagItem("caldav.error.phase").ShouldBeNull();
        activity.GetTagItem("caldav.mutation.state").ShouldBeNull();
        activity.GetTagItem("error.type").ShouldBeNull();
    }

    [Fact]
    public void ExportAllowlist_PreservesEveryClosedOutcomeAndMutationState()
    {
        using var listener = ListenTo(CalendarTelemetry.InstrumentationName);
        using var source = new ActivitySource(CalendarTelemetry.InstrumentationName);
        var processor = new TelemetryActivityAllowlistProcessor();

        foreach (var outcome in new[] { "success", "input_required", "cancelled", "error" })
        {
            using var activity = source.StartActivity("caldav.operation");
            activity.ShouldNotBeNull();
            activity.SetTag("caldav.outcome", outcome);
            activity.Stop();

            processor.OnEnd(activity);

            activity.GetTagItem("caldav.outcome").ShouldBe(outcome);
        }

        foreach (var mutationState in new[] { "not_attempted", "not_committed", "committed", "unknown" })
        {
            using var activity = source.StartActivity("caldav.operation");
            activity.ShouldNotBeNull();
            activity.SetTag("caldav.mutation.state", mutationState);
            activity.Stop();

            processor.OnEnd(activity);

            activity.GetTagItem("caldav.mutation.state").ShouldBe(mutationState);
        }
    }

    [Fact]
    public void ExportAllowlist_NormalizesEverySupportedNumericRepresentation()
    {
        using var listener = ListenTo(OpenTelemetryHostConfiguration.HttpInstrumentationName);
        using var source = new ActivitySource(OpenTelemetryHostConfiguration.HttpInstrumentationName);
        var processor = new TelemetryActivityAllowlistProcessor();

        foreach (var statusCode in new object[] { (byte)207, (short)207, 207, 207L })
        {
            using var activity = source.StartActivity("REPORT");
            activity.ShouldNotBeNull();
            activity.SetTag("http.request.method", "REPORT");
            activity.SetTag("http.response.status_code", statusCode);
            activity.Stop();

            processor.OnEnd(activity);

            Convert.ToInt64(activity.GetTagItem("http.response.status_code")).ShouldBe(207);
        }
    }

    [Fact]
    public void ExportAllowlist_PreservesOnlyControlledErrorTypeShapes()
    {
        foreach (var value in new[]
                 {
                     "timeout", "connection_error", "response_ended", "protocol_error",
                     "internal_error", "caldav.invalid_input", "503"
                 })
        {
            CalendarTelemetryVocabulary.ErrorType(value).ShouldBe(value);
        }

        foreach (var value in new object?[]
                 {
                     null, 503, "caldav.private_resource", "50", "5000", "5a3"
                 })
        {
            CalendarTelemetryVocabulary.ErrorType(value).ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("PROPFIND", "PROPFIND")]
    [InlineData("PRIVATE", "HTTP")]
    public void ExportAllowlist_NormalizesHttpSpansAndRemovesUrls(
        string method,
        string expectedDisplayName)
    {
        using var listener = ListenTo(OpenTelemetryHostConfiguration.HttpInstrumentationName);
        using var source = new ActivitySource(OpenTelemetryHostConfiguration.HttpInstrumentationName);
        using var activity = source.StartActivity("GET https://calendar.example/private.ics");
        activity.ShouldNotBeNull();
        activity.SetTag("http.request.method", "_OTHER");
        activity.SetTag("http.request.method_original", method);
        activity.SetTag("http.request.resend_count", 1);
        activity.SetTag("http.response.status_code", 207);
        activity.SetTag("url.full", "https://calendar.example/private.ics?token=secret");
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.DisplayName.ShouldBe(expectedDisplayName);
        activity.GetTagItem("http.request.method").ShouldBe(expectedDisplayName);
        activity.GetTagItem("http.response.status_code").ShouldBe(207);
        activity.GetTagItem("http.request.resend_count").ShouldBe(1);
        activity.GetTagItem("http.request.method_original").ShouldBeNull();
        activity.GetTagItem("url.full").ShouldBeNull();
    }

    [Fact]
    public void ExportAllowlist_OnlyClosedMarkedObservationsReclassifyHttpNotFound()
    {
        using var listener = ListenTo("DotnetAgents.CalDav.Http");
        using var source = new ActivitySource("DotnetAgents.CalDav.Http");
        using var marked = source.StartActivity("GET marked");
        marked.ShouldNotBeNull();
        marked.SetTag("http.request.method", "GET");
        marked.SetTag("http.response.status_code", 404);
        marked.SetTag("error.type", "404");
        marked.SetTag("caldav.http.request_purpose", "absence_probe");
        marked.SetStatus(ActivityStatusCode.Error);
        marked.Stop();
        using var queryRead = source.StartActivity("GET query");
        queryRead.ShouldNotBeNull();
        queryRead.SetTag("http.request.method", "GET");
        queryRead.SetTag("http.response.status_code", 404);
        queryRead.SetTag("error.type", "404");
        queryRead.SetTag("caldav.http.request_purpose", "query_resource_read");
        queryRead.SetStatus(ActivityStatusCode.Error);
        queryRead.Stop();
        using var unmarked = source.StartActivity("GET unmarked");
        unmarked.ShouldNotBeNull();
        unmarked.SetTag("http.request.method", "GET");
        unmarked.SetTag("http.response.status_code", 404);
        unmarked.SetTag("error.type", "404");
        unmarked.SetStatus(ActivityStatusCode.Error);
        unmarked.Stop();

        var processor = new TelemetryActivityAllowlistProcessor();
        processor.OnEnd(marked);
        processor.OnEnd(queryRead);
        processor.OnEnd(unmarked);

        marked.GetTagItem("http.response.status_code").ShouldBe(404);
        marked.GetTagItem("caldav.http.request_purpose").ShouldBe("absence_probe");
        marked.GetTagItem("caldav.http.observation").ShouldBe("expected_absence");
        marked.GetTagItem("error.type").ShouldBeNull();
        marked.Status.ShouldBe(ActivityStatusCode.Ok);
        queryRead.GetTagItem("http.response.status_code").ShouldBe(404);
        queryRead.GetTagItem("caldav.http.request_purpose").ShouldBe("query_resource_read");
        queryRead.GetTagItem("caldav.http.observation").ShouldBe("resource_disappeared");
        queryRead.GetTagItem("error.type").ShouldBeNull();
        queryRead.Status.ShouldBe(ActivityStatusCode.Ok);
        unmarked.GetTagItem("caldav.http.request_purpose").ShouldBeNull();
        unmarked.GetTagItem("caldav.http.observation").ShouldBeNull();
        unmarked.GetTagItem("error.type").ShouldBe("404");
        unmarked.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Theory]
    [InlineData(200, ActivityStatusCode.Unset)]
    [InlineData(503, ActivityStatusCode.Error)]
    public void ExportAllowlist_PreservesQueryReadPurposeOnEveryWireOutcome(
        int statusCode,
        ActivityStatusCode expectedStatus)
    {
        using var listener = ListenTo("DotnetAgents.CalDav.Http");
        using var source = new ActivitySource("DotnetAgents.CalDav.Http");
        using var activity = source.StartActivity("GET query resource");
        activity.ShouldNotBeNull();
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.response.status_code", statusCode);
        activity.SetTag("caldav.http.request_purpose", "query_resource_read");
        if (statusCode >= 400)
        {
            activity.SetTag("error.type", statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            activity.SetStatus(ActivityStatusCode.Error);
        }
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.GetTagItem("caldav.http.request_purpose").ShouldBe("query_resource_read");
        activity.GetTagItem("caldav.http.observation").ShouldBeNull();
        activity.Status.ShouldBe(expectedStatus);
    }

    [Fact]
    public void ExportAllowlist_CountsRetriesAcrossIndependentRecoveredRequests()
    {
        Activity? stoppedOperation = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name is CalendarTelemetry.InstrumentationName or "DotnetAgents.CalDav.Http",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "caldav.operation")
                    stoppedOperation = activity;
            }
        };
        ActivitySource.AddActivityListener(listener);
        using var httpSource = new ActivitySource("DotnetAgents.CalDav.Http");

        using (var operation = CalendarTelemetry.StartOperation("calendars.list", null))
        {
            operation.ShouldNotBeNull();
            operation.StartPhase(CalendarOperationPhase.Discovery);
            using (var failedAttempt = httpSource.StartActivity("PROPFIND failed"))
            {
                failedAttempt.ShouldNotBeNull();
                failedAttempt.SetTag("http.request.method", "PROPFIND");
                failedAttempt.SetTag("http.response.status_code", 503);
                failedAttempt.SetTag("http.request.resend_count", 0);
                failedAttempt.SetStatus(ActivityStatusCode.Error);
                failedAttempt.Stop();
                new TelemetryActivityAllowlistProcessor().OnEnd(failedAttempt);
                failedAttempt.Status.ShouldBe(ActivityStatusCode.Error);
            }
            using (var recoveredAttempt = httpSource.StartActivity("PROPFIND recovered"))
            {
                recoveredAttempt.ShouldNotBeNull();
                recoveredAttempt.SetTag("http.request.method", "PROPFIND");
                recoveredAttempt.SetTag("http.response.status_code", 207);
                recoveredAttempt.SetTag("http.request.resend_count", 1);
                recoveredAttempt.Stop();
                new TelemetryActivityAllowlistProcessor().OnEnd(recoveredAttempt);
                recoveredAttempt.Status.ShouldBe(ActivityStatusCode.Unset);
            }
            using (var secondRecoveredAttempt = httpSource.StartActivity("REPORT recovered"))
            {
                secondRecoveredAttempt.ShouldNotBeNull();
                secondRecoveredAttempt.SetTag("http.request.method", "REPORT");
                secondRecoveredAttempt.SetTag("http.response.status_code", 207);
                secondRecoveredAttempt.SetTag("http.request.resend_count", 1);
                secondRecoveredAttempt.Stop();
                new TelemetryActivityAllowlistProcessor().OnEnd(secondRecoveredAttempt);
            }
            operation.Complete("success");
        }

        stoppedOperation.ShouldNotBeNull();
        stoppedOperation.GetTagItem("caldav.outcome").ShouldBe("success");
        stoppedOperation.GetTagItem("caldav.transport.recovered").ShouldBe(true);
        stoppedOperation.GetTagItem("caldav.transport.retry_count").ShouldBe(2);
        stoppedOperation.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public void ExportAllowlist_DoesNotClaimRecoveryWhenOperationUltimatelyFails()
    {
        Activity? stoppedOperation = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name is CalendarTelemetry.InstrumentationName or "DotnetAgents.CalDav.Http",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "caldav.operation")
                    stoppedOperation = activity;
            }
        };
        ActivitySource.AddActivityListener(listener);
        using var httpSource = new ActivitySource("DotnetAgents.CalDav.Http");
        var processor = new TelemetryActivityAllowlistProcessor();

        using (var operation = CalendarTelemetry.StartOperation("calendars.list", null))
        {
            operation.ShouldNotBeNull();
            operation.StartPhase(CalendarOperationPhase.Discovery);
            using var recoveredAttempt = httpSource.StartActivity("PROPFIND recovered");
            recoveredAttempt.ShouldNotBeNull();
            recoveredAttempt.SetTag("http.request.method", "PROPFIND");
            recoveredAttempt.SetTag("http.response.status_code", 207);
            recoveredAttempt.SetTag("http.request.resend_count", 1);
            recoveredAttempt.Stop();
            processor.OnEnd(recoveredAttempt);
            operation.Complete(
                "error",
                "upstream_unavailable",
                "upstream",
                mutationState: null,
                errorPhase: "execution",
                retryable: true);
        }

        stoppedOperation.ShouldNotBeNull();
        processor.OnEnd(stoppedOperation);
        stoppedOperation.GetTagItem("caldav.outcome").ShouldBe("error");
        stoppedOperation.GetTagItem("caldav.transport.retry_count").ShouldBe(1);
        stoppedOperation.GetTagItem("caldav.transport.recovered").ShouldBeNull();
        stoppedOperation.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public void ExportAllowlist_UsesStableProductOperationName()
    {
        using var listener = ListenTo(CalendarTelemetry.InstrumentationName);
        using var source = new ActivitySource(CalendarTelemetry.InstrumentationName);
        using var activity = source.StartActivity("caldav.phase.fetch");
        activity.ShouldNotBeNull();
        activity.DisplayName = "private resource identifier";
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.DisplayName.ShouldBe("caldav.phase.fetch");
    }

    [Fact]
    public void Operation_WithoutAllData_DoesNotAttachDimensions()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.PropagationData
        };
        ActivitySource.AddActivityListener(listener);

        using var operation = CalendarTelemetry.StartOperation(
            "todos.complete",
            CalendarTelemetryEntityKind.Todo);
        operation.ShouldNotBeNull();
        operation.StartPhase(CalendarOperationPhase.Reconcile);
        operation.Complete("error", "private-code", "private-category", "changed");
        operation.Fail(new InvalidOperationException("secret"));
    }

    [Fact]
    public void Operation_FailureRecordsOnlyControlledExceptionClassification()
    {
        var stoppedOperations = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "caldav.operation")
                    stoppedOperations.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);

        var cases = new (Exception Exception, string ErrorType)[]
        {
            (new TimeoutException("secret timeout"), "timeout"),
            (new TaskCanceledException("secret cancellation"), "timeout"),
            (new HttpRequestException(
                HttpRequestError.ResponseEnded,
                "secret partial body"), "response_ended"),
            (new HttpRequestException("secret endpoint"), "connection_error"),
            (new IOException("secret stream"), "connection_error"),
            (new CalendarDiscoveryProtocolException("secret response"), "protocol_error"),
            (new InvalidOperationException("secret message"), "internal_error")
        };

        foreach (var @case in cases)
        {
            using var operation = CalendarTelemetry.StartOperation("todos.complete", null);
            operation.ShouldNotBeNull();
            operation.StartPhase(CalendarOperationPhase.Reconcile);
            operation.Fail(@case.Exception);
        }

        stoppedOperations.Count.ShouldBe(cases.Length);
        foreach (var (activity, @case) in stoppedOperations.Zip(cases))
        {
            activity.GetTagItem("caldav.outcome").ShouldBe("error");
            activity.GetTagItem("error.type").ShouldBe(@case.ErrorType);
            activity.Status.ShouldBe(ActivityStatusCode.Error);
            activity.StatusDescription.ShouldBeNull();
        }
    }

    [Fact]
    public void LogAllowlist_PreservesCorrelationAndSafeFieldsWithoutMessagesOrExceptions()
    {
        var record = (LogRecord)Activator.CreateInstance(typeof(LogRecord), nonPublic: true)!;
        record.CategoryName = "DotnetAgents.CalDav.Core.Internal.CalDavClient";
        record.Body = "CalDAV response contained https://calendar.example/private.ics";
        record.FormattedMessage = "Authorization: Basic secret";
        record.Exception = new InvalidOperationException("secret resource body");
        record.TraceId = ActivityTraceId.CreateRandom();
        record.SpanId = ActivitySpanId.CreateRandom();
        record.Attributes =
        [
            new("Code", "principal_unavailable"),
            new("Phase", "selectionDiscoveryCapability"),
            new("Href", "https://calendar.example/private.ics"),
            new("Authorization", "Basic secret"),
            new("{OriginalFormat}", "CalDAV response from {Href}")
        ];
        var traceId = record.TraceId;
        var spanId = record.SpanId;

        new TelemetryLogAllowlistProcessor().OnEnd(record);

        record.Body.ShouldBe("CalDAV diagnostic");
        record.FormattedMessage.ShouldBeNull();
        record.Exception.ShouldBeNull();
        record.Attributes.ShouldBe([
            new KeyValuePair<string, object?>("Code", "principal_unavailable"),
            new KeyValuePair<string, object?>("Phase", "selectionDiscoveryCapability")
        ]);
        record.TraceId.ShouldBe(traceId);
        record.SpanId.ShouldBe(spanId);
    }

    [Fact]
    public void QueryTelemetryAllowlistKeepsOnlyClosedModesAndBoundedWorkCounts()
    {
        using var listener = ListenTo(CalendarTelemetry.InstrumentationName);
        using var source = new ActivitySource(CalendarTelemetry.InstrumentationName);
        using var activity = source.StartActivity("caldav.query");
        activity.ShouldNotBeNull();
        activity.SetTag("caldav.query.mode", "start");
        activity.SetTag("caldav.query.fetch_mode", "multiget");
        activity.SetTag("caldav.query.fallback_reason", "private");
        activity.SetTag("caldav.query.direct_get_resource_count", 4L);
        activity.SetTag("caldav.query.direct_get_attempt_count", 5L);
        activity.SetTag("caldav.query.disappeared_resource_count", 1L);
        activity.SetTag("caldav.query.snapshot_lookup_count", 1L);
        activity.SetTag("caldav.query.serialization_count", 0L);
        activity.SetTag("caldav.query.candidate_count", -1L);
        activity.SetTag("caldav.query.admission_envelope_serialization_count", 1L);
        activity.SetTag("caldav.query.final_materialization_count", 1L);
        activity.SetTag("caldav.query.phase", "private_phase");
        activity.SetTag("caldav.query.cursor", "private-cursor");
        activity.SetTag("caldav.query.href", "https://cal.example/private.ics");
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.GetTagItem("caldav.query.mode").ShouldBe("start");
        activity.GetTagItem("caldav.query.fetch_mode").ShouldBe("multiget");
        activity.GetTagItem("caldav.query.fallback_reason").ShouldBeNull();
        activity.GetTagItem("caldav.query.direct_get_resource_count").ShouldBe(4L);
        activity.GetTagItem("caldav.query.direct_get_attempt_count").ShouldBe(5L);
        activity.GetTagItem("caldav.query.disappeared_resource_count").ShouldBe(1L);
        activity.GetTagItem("caldav.query.snapshot_lookup_count").ShouldBe(1L);
        activity.GetTagItem("caldav.query.serialization_count").ShouldBe(0L);
        activity.GetTagItem("caldav.query.candidate_count").ShouldBeNull();
        activity.GetTagItem("caldav.query.admission_envelope_serialization_count").ShouldBeNull();
        activity.GetTagItem("caldav.query.final_materialization_count").ShouldBeNull();
        activity.GetTagItem("caldav.query.phase").ShouldBeNull();
        activity.GetTagItem("caldav.query.cursor").ShouldBeNull();
        activity.GetTagItem("caldav.query.href").ShouldBeNull();
    }

    [Theory]
    [InlineData("direct_get_fallback")]
    [InlineData("mixed")]
    public void QueryTelemetryAllowlistKeepsEveryClosedFallbackModeAndReason(string mode)
    {
        using var listener = ListenTo(CalendarTelemetry.InstrumentationName);
        using var source = new ActivitySource(CalendarTelemetry.InstrumentationName);
        using var activity = source.StartActivity("caldav.operation");
        activity.ShouldNotBeNull();
        activity.SetTag("caldav.query.mode", "start");
        activity.SetTag("caldav.query.fetch_mode", mode);
        activity.SetTag("caldav.query.fallback_reason", "multiget_unavailable");
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.GetTagItem("caldav.query.fetch_mode").ShouldBe(mode);
        activity.GetTagItem("caldav.query.fallback_reason").ShouldBe("multiget_unavailable");
    }

    [Fact]
    public void QueryDimensionsAreRemovedFromNonCalendarInstrumentation()
    {
        using var listener = ListenTo(OpenTelemetryHostConfiguration.McpInstrumentationName);
        using var source = new ActivitySource(OpenTelemetryHostConfiguration.McpInstrumentationName);
        using var activity = source.StartActivity("tools/call");
        activity.ShouldNotBeNull();
        activity.SetTag("mcp.method.name", "tools/call");
        activity.SetTag("caldav.query.phase", "fetch");
        activity.SetTag("caldav.query.snapshot_count", 1L);
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.GetTagItem("caldav.query.phase").ShouldBeNull();
        activity.GetTagItem("caldav.query.snapshot_count").ShouldBeNull();
    }

    [Theory]
    [InlineData("discovery")]
    [InlineData("candidate")]
    [InlineData("fetch")]
    [InlineData("evaluation")]
    [InlineData("serialization")]
    [InlineData("reservation")]
    [InlineData("snapshot_lookup")]
    [InlineData("page_admission")]
    public void QueryPhaseAllowlistPreservesEveryClosedValue(string phase)
    {
        using var listener = ListenTo(CalendarTelemetry.InstrumentationName);
        using var source = new ActivitySource(CalendarTelemetry.InstrumentationName);
        using var activity = source.StartActivity("caldav.query.phase");
        activity.ShouldNotBeNull();
        activity.SetTag("caldav.query.phase", phase);
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.GetTagItem("caldav.query.phase").ShouldBe(phase);
    }

    [Fact]
    public void QueryCounterAllowlistAcceptsIntegralRepresentationsAndExactBoundsOnly()
    {
        using var listener = ListenTo(CalendarTelemetry.InstrumentationName);
        using var source = new ActivitySource(CalendarTelemetry.InstrumentationName);
        using var activity = source.StartActivity("caldav.operation");
        activity.ShouldNotBeNull();
        activity.SetTag("caldav.query.mode", "continue");
        activity.SetTag("caldav.query.candidate_count", (byte)1);
        activity.SetTag("caldav.query.multiget_resource_count", (short)2);
        activity.SetTag("caldav.query.snapshot_count", 3);
        activity.SetTag("caldav.query.evaluation_count", 4L);
        activity.SetTag("caldav.query.serialization_count", 100_000_000L);
        activity.SetTag("caldav.query.snapshot_lookup_count", 100_000_001L);
        activity.SetTag("caldav.query.page_admission_count", "private");
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.GetTagItem("caldav.query.mode").ShouldBe("continue");
        activity.GetTagItem("caldav.query.candidate_count").ShouldBe(1L);
        activity.GetTagItem("caldav.query.multiget_resource_count").ShouldBe(2L);
        activity.GetTagItem("caldav.query.snapshot_count").ShouldBe(3L);
        activity.GetTagItem("caldav.query.evaluation_count").ShouldBe(4L);
        activity.GetTagItem("caldav.query.serialization_count").ShouldBe(100_000_000L);
        activity.GetTagItem("caldav.query.snapshot_lookup_count").ShouldBeNull();
        activity.GetTagItem("caldav.query.page_admission_count").ShouldBeNull();
    }

    [Fact]
    public void FailedRetryAndOrphanRetryDoNotClaimRecovery()
    {
        using var calendarListener = ListenTo(CalendarTelemetry.InstrumentationName);
        using var httpListener = ListenTo(OpenTelemetryHostConfiguration.HttpInstrumentationName);
        using var calendarSource = new ActivitySource(CalendarTelemetry.InstrumentationName);
        using var httpSource = new ActivitySource(OpenTelemetryHostConfiguration.HttpInstrumentationName);
        var processor = new TelemetryActivityAllowlistProcessor();

        using (var orphan = httpSource.StartActivity("HTTP"))
        {
            orphan.ShouldNotBeNull();
            orphan.SetTag("http.request.resend_count", 1);
            orphan.Stop();
            processor.OnEnd(orphan);
        }

        using var operation = calendarSource.StartActivity("caldav.operation");
        operation.ShouldNotBeNull();
        operation.SetTag("caldav.outcome", "success");
        using (var failed = httpSource.StartActivity("HTTP"))
        {
            failed.ShouldNotBeNull();
            failed.SetStatus(ActivityStatusCode.Error);
            failed.SetTag("http.request.resend_count", (short)1);
            failed.Stop();
            processor.OnEnd(failed);
        }
        operation.Stop();
        processor.OnEnd(operation);

        operation.GetTagItem("caldav.transport.retry_count").ShouldBe(1);
        operation.GetTagItem("caldav.transport.recovered").ShouldBeNull();
    }

    [Fact]
    public void UnknownInstrumentationCannotRetainCalendarQueryDimensions()
    {
        const string sourceName = "Private.Unknown.Instrumentation";
        using var listener = ListenTo(sourceName);
        using var source = new ActivitySource(sourceName);
        using var activity = source.StartActivity("private operation");
        activity.ShouldNotBeNull();
        activity.SetTag("caldav.query.mode", "start");
        activity.SetTag("private.href", "https://cal.example/private.ics");
        activity.Stop();

        new TelemetryActivityAllowlistProcessor().OnEnd(activity);

        activity.GetTagItem("caldav.query.mode").ShouldBeNull();
        activity.GetTagItem("private.href").ShouldBeNull();
    }

    private static ActivityListener ListenTo(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

[CollectionDefinition("TelemetryActivityCollection", DisableParallelization = true)]
public sealed class TelemetryActivityCollection;
