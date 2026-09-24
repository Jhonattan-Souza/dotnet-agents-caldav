using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Core.Services;
using DotnetAgents.CalDav.Mcp.Hosting;
using ModelContextProtocol.Protocol;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>
/// Discloses possible RFC 6638 server scheduling side effects for scheduling-governed mutations
/// when the opt-in server-managed scheduling mode is configured. Storage-only results are unchanged.
/// </summary>
internal static class CalendarSchedulingDisclosure
{
    internal const string PropertyName = "schedulingSideEffects";
    internal const string None = "none";
    internal const string Possible = "possible";

    internal const string ConfirmationWarning =
        "Scheduling notice: server-managed scheduling is enabled, so the CalDAV server may automatically send "
        + "invitations, updates, replies, or cancellations to organizers and attendees as a result of this change.";

    private static readonly AsyncLocal<bool> Active = new();

    internal static bool IsActive => Active.Value;

    /// <summary>Tools whose writes pass the scheduling safety lock.</summary>
    internal static bool Governs(string? toolName) => toolName is
        "events.create" or "events.patch" or "todos.create" or "todos.patch" or "todos.complete"
        or "calendar_occurrences.add" or "calendar_occurrences.exclude"
        or "calendar_occurrences.restore_exclusion" or "calendar_occurrences.cancel"
        or "calendar_occurrences.restore_cancellation" or "calendar_resources.delete" or "calendars.delete"
        or "calendar_resources.exact_create" or "calendar_resources.exact_replace";

    internal static Scope Attach(bool active)
    {
        var previous = Active.Value;
        Active.Value = active;
        return new Scope(previous);
    }

    /// <summary>Appends the scheduling warning to an MRTR confirmation message when disclosure is active.</summary>
    internal static string WithConfirmationWarning(string message) =>
        IsActive ? string.Concat(message, " ", ConfirmationWarning) : message;

    /// <summary>
    /// Adds the typed disclosure to a result whose write committed or may have committed.
    /// Results that attempted nothing or were definitively rejected carry no disclosure.
    /// </summary>
    internal static void Apply(CallToolResult result)
    {
        if (!IsActive
            || result.StructuredContent is not { ValueKind: JsonValueKind.Object } structured
            || !structured.TryGetProperty("mutationState", out var mutationState)
            || mutationState.ValueKind != JsonValueKind.String
            || mutationState.GetString() is not ("committed" or "unknown"))
            return;

        var value = CalendarOperationProgress.SchedulingSideEffectsPossible ? Possible : None;
        var body = JsonObject.Create(structured)!;
        body[PropertyName] = value;
        result.StructuredContent = JsonSerializer.SerializeToElement(body);
        CalendarTelemetry.ObserveSchedulingSideEffects(value);
    }

    internal sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
