using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static readonly AsyncLocal<IReadOnlyList<CalendarInputViolation>?> PendingViolations = new();

    internal static CallToolResult WithViolations(
        Func<CallToolResult> createResult, IReadOnlyList<CalendarInputViolation> violations)
    {
        var previous = PendingViolations.Value;
        PendingViolations.Value = violations;
        try
        {
            return createResult();
        }
        finally
        {
            PendingViolations.Value = previous;
        }
    }

    internal CallToolResult FinalizeResult() => FinalizeBounded(CreatePayloadError);

    internal CallToolResult FinalizeBounded(
        Func<int, bool, CalendarToolResult> createPayloadError)
    {
        if (PendingViolations.Value is { } violations)
            CalendarErrorViolations.Attach(Value, violations);
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

    private CalendarToolResult CreatePayloadError(int byteCount, bool humanReadable)
    {
        var facts = new CalendarStructuredErrorFacts(
            CalendarTelemetryErrorCode.PayloadTooLarge,
            CalendarTelemetryErrorCategory.LimitsAndAdmission,
            CalendarTelemetryErrorPhase.AdmissionAndPayload,
            false);
        var body = new JsonObject
        {
            ["code"] = facts.CodeName,
            ["category"] = facts.CategoryName,
            ["phase"] = facts.PhaseName,
            ["retryable"] = false,
            ["message"] = humanReadable
                ? "The human-readable result exceeds the safe payload limit."
                : "The serialized result exceeds the safe payload limit.",
            ["limits"] = new JsonObject { ["byteCount"] = byteCount }
        };
        if (Value.StructuredContent is { } structured
            && structured.TryGetProperty("mutationState", out var mutation))
            body["mutationState"] = mutation.GetString();
        return new CalendarToolResult(new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(body),
            Content = []
        }, new CalendarTerminalFacts(facts, Facts.MutationState));
    }
}
