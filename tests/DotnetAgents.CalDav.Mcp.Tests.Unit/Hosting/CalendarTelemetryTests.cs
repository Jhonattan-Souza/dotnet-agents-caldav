using System.Diagnostics;
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
        activity.GetTagItem("error.type").ShouldBe("System.Net.Http.HttpRequestException");
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
    public void Operation_FailureRecordsOnlyExceptionType()
    {
        Activity? stoppedOperation = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "caldav.operation")
                    stoppedOperation = activity;
            }
        };
        ActivitySource.AddActivityListener(listener);

        using (var operation = CalendarTelemetry.StartOperation("todos.complete", null))
        {
            operation.ShouldNotBeNull();
            operation.StartPhase(CalendarOperationPhase.Reconcile);
            operation.Fail(new InvalidOperationException("secret message"));
        }

        stoppedOperation.ShouldNotBeNull();
        stoppedOperation.GetTagItem("caldav.outcome").ShouldBe("error");
        stoppedOperation.GetTagItem("error.type").ShouldBe(typeof(InvalidOperationException).FullName);
        stoppedOperation.Status.ShouldBe(ActivityStatusCode.Error);
        stoppedOperation.StatusDescription.ShouldBeNull();
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
