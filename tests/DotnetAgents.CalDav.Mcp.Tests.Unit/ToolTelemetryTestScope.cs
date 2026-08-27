using System.Diagnostics;
using System.Text.Json;
using DotnetAgents.CalDav.Mcp.Hosting;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

internal static class ToolTelemetryTestScope
{
    internal static async Task<(CallToolResult Result, Activity Operation)> CaptureAsync(
        string toolName,
        Func<Task<CallToolResult>> execute)
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

        CallToolResult result;
        using (var operation = CalendarTelemetry.StartOperation(toolName, null))
        using (CalendarTelemetry.Attach(operation))
        {
            operation.ShouldNotBeNull();
            result = await execute();
            operation.Complete(result.IsError == true
                ? CalendarOperationOutcome.Error
                : CalendarOperationOutcome.Success);
        }

        stoppedOperation.ShouldNotBeNull();
        return (result, stoppedOperation);
    }

    internal static void ShouldMatchStructuredError(this Activity operation, JsonElement structured)
    {
        operation.GetTagItem("caldav.error.code").ShouldBe(structured.GetProperty("code").GetString());
        operation.GetTagItem("caldav.error.category").ShouldBe(structured.GetProperty("category").GetString());
        operation.GetTagItem("caldav.error.phase").ShouldBe(structured.GetProperty("phase").GetString());
        operation.GetTagItem("caldav.error.retryable").ShouldBe(structured.GetProperty("retryable").GetBoolean());
    }
}
