using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using ModelContextProtocol.Protocol;

namespace DotnetAgents.CalDav.Mcp.Tools;

internal readonly record struct CalendarTerminalFacts(
    CalendarStructuredErrorFacts? Error = null,
    CalendarMutationState? MutationState = null)
{
    internal void Observe()
    {
        if (Error is { } error)
            CalendarTelemetry.ObserveStructuredError(error);
        if (MutationState is { } mutationState)
            CalendarTelemetry.ObserveMutationState(mutationState);
    }
}

internal readonly record struct CalendarToolResult(
    CallToolResult Value,
    CalendarTerminalFacts Facts)
{
    internal static CalendarToolResult Success(CallToolResult value) => new(value, default);

    internal static CalendarToolResult Success(CallToolResult value, CalendarMutationState mutationState) =>
        new(value, new CalendarTerminalFacts(MutationState: mutationState));

    internal static CalendarToolResult Error(
        CallToolResult value,
        CalendarStructuredErrorFacts error) =>
        new(value, new CalendarTerminalFacts(error));

    internal static CalendarToolResult Error(
        CallToolResult value,
        CalendarStructuredErrorFacts error,
        CalendarMutationState mutationState) =>
        new(value, new CalendarTerminalFacts(error, mutationState));

    internal CallToolResult FinalizeResult()
    {
        Facts.Observe();
        return Value;
    }

    internal CallToolResult FinalizeBounded(
        Func<int, bool, CalendarToolResult> createPayloadError)
    {
        var terminal = this;
        var bounded = CalendarQueryToolSupport.EnsureBoundedResult(
            Value,
            (byteCount, humanReadable) =>
            {
                terminal = createPayloadError(byteCount, humanReadable);
                return terminal.Value;
            });
        terminal.Facts.Observe();
        return bounded;
    }
}
