using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>High-level Calendar discovery service used by unified MCP tools.</summary>
public interface ICalendarService
{
    /// <summary>Lists the Calendars in the configured Calendar Scope.</summary>
    Task<CalendarDiscoveryResult> GetCalendarsAsync(CancellationToken cancellationToken);

    /// <summary>Resolves the configured default Calendar for one Entity Kind without fallback.</summary>
    Task<CalendarSelectionResult> ResolveDefaultCalendarAsync(
        CalendarEntityKind entityKind,
        CancellationToken cancellationToken);

    /// <summary>Queries persisted Event and To-do snapshots within an explicit Calendar Scope.</summary>
    Task<CalendarEntityQueryResult> QueryEntitiesAsync(
        CalendarEntityQuery query,
        CancellationToken cancellationToken);

    /// <summary>Queries bounded derived Event and To-do Occurrences.</summary>
    Task<CalendarOccurrenceQueryResult> QueryOccurrencesAsync(
        CalendarOccurrenceQuery query,
        CancellationToken cancellationToken);

    /// <summary>Reads one authoritative Calendar Object Resource snapshot.</summary>
    Task<CalendarResourceRead> GetResourceAsync(string href, CancellationToken cancellationToken);

    /// <summary>Creates one caller-authored complete resource at one explicit destination href.</summary>
    Task<CalendarExactResourceResult> ExactCreateResourceAsync(
        CalendarExactCreateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Validates an exact create without dispatching a write.</summary>
    Task<CalendarExactResourceReviewResult> ReviewExactCreateResourceAsync(
        CalendarExactCreateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Replaces one reviewed resource with caller-authored complete content.</summary>
    Task<CalendarExactResourceResult> ExactReplaceResourceAsync(
        CalendarExactReplaceRequest request,
        CancellationToken cancellationToken);

    /// <summary>Validates an exact replacement without dispatching a write.</summary>
    Task<CalendarExactResourceReviewResult> ReviewExactReplaceResourceAsync(
        CalendarExactReplaceRequest request,
        CancellationToken cancellationToken);

    /// <summary>Atomically moves one reviewed resource to one explicit destination href.</summary>
    Task<CalendarExactResourceResult> ExactMoveResourceAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken);

    /// <summary>Validates an exact move without dispatching a write.</summary>
    Task<CalendarExactResourceReviewResult> ReviewExactMoveResourceAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken);

    /// <summary>Creates one complete Event, including typed recurrence, and returns the observed server revision.</summary>
    Task<CalendarEntityCreateResult> CreateEventAsync(
        CalendarEventCreateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Creates one complete To-do, including typed recurrence, and returns the observed server revision.</summary>
    Task<CalendarEntityCreateResult> CreateTodoAsync(
        CalendarTodoCreateRequest request,
        CancellationToken cancellationToken);

    /// <summary>Applies one revision-bound semantic patch to an Event.</summary>
    Task<CalendarEntityPatchResult> PatchEventAsync(
        CalendarEventPatchRequest request,
        CancellationToken cancellationToken);

    /// <summary>Applies one revision-bound semantic patch to a To-do.</summary>
    Task<CalendarEntityPatchResult> PatchTodoAsync(
        CalendarTodoPatchRequest request,
        CancellationToken cancellationToken);

    /// <summary>Validates one Event patch losslessly without dispatching a write.</summary>
    Task<CalendarEntityPatchReviewResult> ReviewEventPatchAsync(
        CalendarEventPatchRequest request,
        CancellationToken cancellationToken);

    /// <summary>Validates one To-do patch losslessly without dispatching a write.</summary>
    Task<CalendarEntityPatchReviewResult> ReviewTodoPatchAsync(
        CalendarTodoPatchRequest request,
        CancellationToken cancellationToken);

    /// <summary>Adds one explicit RDATE identity to one revision-bound Recurrence Set.</summary>
    Task<CalendarEntityPatchResult> AddOccurrenceAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken);

    /// <summary>Adds one exact EXDATE without removing a preserved Recurrence Override.</summary>
    Task<CalendarEntityPatchResult> ExcludeOccurrenceAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken);

    /// <summary>Removes only the exact EXDATE addressed by one original Recurrence Identity.</summary>
    Task<CalendarEntityPatchResult> RestoreOccurrenceExclusionAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken);

    /// <summary>Creates or updates one complete cancelled individual Recurrence Override.</summary>
    Task<CalendarEntityPatchResult> CancelOccurrenceAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken);

    /// <summary>Clears only cancelled status while preserving the addressed individual override.</summary>
    Task<CalendarEntityPatchResult> RestoreOccurrenceCancellationAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken);

    /// <summary>Records one injected completion instant for a non-recurring To-do or one recurring Occurrence.</summary>
    Task<CalendarEntityPatchResult> CompleteTodoAsync(
        CalendarTodoCompletionRequest request,
        CancellationToken cancellationToken);

    /// <summary>Atomically moves one reviewed resource to a selected compatible Calendar.</summary>
    Task<CalendarResourceMoveResult> MoveResourceAsync(
        CalendarResourceMoveRequest request,
        CancellationToken cancellationToken) => Task.FromResult(new CalendarResourceMoveResult(
            CalendarResourceMoveCode.UnsupportedCapability,
            CalendarMutationState.NotAttempted,
            Phase: CalendarResourceMovePhase.SelectionDiscoveryCapability));

    /// <summary>Deletes one reviewed resource and succeeds only after verified absence.</summary>
    Task<CalendarResourceDeleteResult> DeleteResourceAsync(
        CalendarResourceRevisionReference revision,
        CancellationToken cancellationToken);
}
