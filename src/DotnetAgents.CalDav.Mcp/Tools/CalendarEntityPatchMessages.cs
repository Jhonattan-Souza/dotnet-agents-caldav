using System.Collections.Frozen;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Fixed per-outcome explanations shared by patch, completion, and Occurrence mutation tools.</summary>
internal static class CalendarEntityPatchMessages
{
    private static readonly FrozenDictionary<CalendarEntityPatchCode, string> Messages =
        new Dictionary<CalendarEntityPatchCode, string>
        {
            [CalendarEntityPatchCode.InvalidInput] = "The request does not fit this Calendar Entity.",
            [CalendarEntityPatchCode.InvalidCalendarData] = "The change would leave the Calendar Entity invalid.",
            [CalendarEntityPatchCode.NotFound] = "The resource or the requested Occurrence was not found.",
            [CalendarEntityPatchCode.RemovalNotFound] = "No requested collection occurrence matched.",
            [CalendarEntityPatchCode.RemovalAmbiguous] = "The requested collection removal was ambiguous.",
            [CalendarEntityPatchCode.OutsideScope] = "The resource is outside the configured Calendar Scope.",
            [CalendarEntityPatchCode.EntityKindMismatch] = "The resource holds a different Entity Kind than this tool changes.",
            [CalendarEntityPatchCode.OpaqueResource] = "The resource cannot be projected safely, so it cannot be changed semantically.",
            [CalendarEntityPatchCode.RecurrenceUnevaluable] = "The Recurrence Set could not be evaluated.",
            [CalendarEntityPatchCode.Conflict] = "The resource changed after the snapshot was read; read it again and retry with the new revision.",
            [CalendarEntityPatchCode.ConcurrencyUnavailable] = "The resource has no strong Entity Tag, so a revision-bound write is unsafe.",
            [CalendarEntityPatchCode.UnsupportedCapability] = "The server or this resource does not support the requested change.",
            [CalendarEntityPatchCode.PayloadTooLarge] = "The changed resource would exceed the safe payload limit.",
            [CalendarEntityPatchCode.LimitExhausted] = "The Calendar Entity patch exceeded its execution time limit.",
            [CalendarEntityPatchCode.UpstreamUnauthorized] = "The Calendar mutation was not authorized.",
            [CalendarEntityPatchCode.UpstreamForbidden] = "The Calendar mutation was forbidden.",
            [CalendarEntityPatchCode.UpstreamRateLimited] = "The Calendar mutation is rate limited.",
            [CalendarEntityPatchCode.UpstreamUnavailable] = "The Calendar mutation is unavailable.",
            [CalendarEntityPatchCode.UpstreamProtocolError] = "The Calendar mutation returned an invalid response.",
            [CalendarEntityPatchCode.FidelityFailure] = "The committed server revision differs from the requested semantics.",
            [CalendarEntityPatchCode.CommittedButUnverified] = "The committed Calendar mutation could not be verified.",
            [CalendarEntityPatchCode.CommittedButConcurrencyUnavailable] = "The committed server revision has no strong Entity Tag.",
            [CalendarEntityPatchCode.TemporalUnresolved] = "A stored time zone could not be resolved.",
            [CalendarEntityPatchCode.CompletionStateConflict] = "The stored completion fields contradict each other, so completion is unsafe."
        }.ToFrozenDictionary();

    internal static string Describe(CalendarEntityPatchCode code) =>
        Messages.TryGetValue(code, out var message) ? message : "The Calendar mutation outcome is indeterminate.";

    /// <summary>Maps semantic target and snapshot reasons to the top-level Occurrence-tool arguments.</summary>
    internal static string? ResolveOccurrencePointer(string pointer) =>
        pointer.StartsWith("/snapshot/", StringComparison.Ordinal) ? pointer
        : pointer.StartsWith("/target/recurrenceIdentity", StringComparison.Ordinal) ? pointer["/target".Length..]
        : null;
}
