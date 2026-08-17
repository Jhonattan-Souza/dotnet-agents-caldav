namespace DotnetAgents.CalDav.Core.Models;

/// <summary>One revision-bound mutation of an original Recurrence Identity.</summary>
public sealed record CalendarOccurrenceMutationRequest(
    CalendarResourceRevisionReference Snapshot,
    CalendarTemporalValue RecurrenceIdentity);
