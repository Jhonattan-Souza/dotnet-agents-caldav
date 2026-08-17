using System.Net.Http.Headers;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarEntityPatchEngine(
    ICalendarClient calendarClient,
    Func<string, CalendarEntityKind, CancellationToken, Task<CalendarResourceRead>> readResource,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan PreDispatchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(30);
    private const int MaximumAuthoritativeBytes = 4 * 1024 * 1024;

    public Task<CalendarEntityPatchResult> PatchEventAsync(
        CalendarEventPatchRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
            token => PatchAsync(request.Snapshot, request.Target, request.Patch, CalendarEntityKind.Event, token),
            DeadlineFailure,
            cancellationToken);

    public Task<CalendarEntityPatchResult> PatchTodoAsync(
        CalendarTodoPatchRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
            token => PatchAsync(request.Snapshot, request.Target, EventPatch(request.Patch), CalendarEntityKind.Todo, token),
            DeadlineFailure,
            cancellationToken);

    public Task<CalendarEntityPatchReviewResult> ReviewEventPatchAsync(
        CalendarEventPatchRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
            token => ReviewAsync(request.Snapshot, request.Target, request.Patch, CalendarEntityKind.Event, token),
            ReviewDeadlineFailure,
            cancellationToken);

    public Task<CalendarEntityPatchReviewResult> ReviewTodoPatchAsync(
        CalendarTodoPatchRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
            token => ReviewAsync(request.Snapshot, request.Target, EventPatch(request.Patch), CalendarEntityKind.Todo, token),
            ReviewDeadlineFailure,
            cancellationToken);

    public Task<CalendarEntityPatchResult> AddOccurrenceAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
            token => MutateOccurrenceWithinDeadlineAsync(request, OccurrenceMutation.Add, token),
            DeadlineFailure,
            cancellationToken);

    public Task<CalendarEntityPatchResult> ExcludeOccurrenceAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
            token => MutateOccurrenceWithinDeadlineAsync(request, OccurrenceMutation.Exclude, token),
            DeadlineFailure,
            cancellationToken);

    public Task<CalendarEntityPatchResult> RestoreOccurrenceExclusionAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
            token => MutateOccurrenceWithinDeadlineAsync(request, OccurrenceMutation.RestoreExclusion, token),
            DeadlineFailure,
            cancellationToken);

    public Task<CalendarEntityPatchResult> CancelOccurrenceAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
            token => MutateOccurrenceWithinDeadlineAsync(request, OccurrenceMutation.Cancel, token),
            DeadlineFailure,
            cancellationToken);

    public Task<CalendarEntityPatchResult> RestoreOccurrenceCancellationAsync(
        CalendarOccurrenceMutationRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
            token => MutateOccurrenceWithinDeadlineAsync(request, OccurrenceMutation.RestoreCancellation, token),
            DeadlineFailure,
            cancellationToken);

    public Task<CalendarEntityPatchResult> CompleteTodoAsync(
        CalendarTodoCompletionRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
            token => CompleteTodoWithinDeadlineAsync(request, token),
            DeadlineFailure,
            cancellationToken);

    private async Task<T> ExecuteWithinPreDispatchDeadlineAsync<T>(
        Func<CancellationToken, Task<T>> execute,
        Func<T> deadlineFailure,
        CancellationToken callerCancellationToken)
    {
        using var deadline = new CancellationTokenSource(PreDispatchTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken, deadline.Token);
        try
        {
            return await execute(linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !callerCancellationToken.IsCancellationRequested)
        {
            return deadlineFailure();
        }
    }

    private async Task<CalendarEntityPatchResult> PatchAsync(
        CalendarResourceRevisionReference revision,
        CalendarMutationTarget target,
        CalendarEventPatch patch,
        CalendarEntityKind expectedKind,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(revision, target, patch, expectedKind, cancellationToken);
        if (prepared.Outcome is not null)
            return prepared.Outcome;
        return await DispatchAsync(revision, prepared.AuthoritativeUtf8!, expectedKind, cancellationToken);
    }

    private async Task<CalendarEntityPatchReviewResult> ReviewAsync(
        CalendarResourceRevisionReference revision,
        CalendarMutationTarget target,
        CalendarEventPatch patch,
        CalendarEntityKind expectedKind,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(revision, target, patch, expectedKind, cancellationToken);
        return prepared.Outcome is not null
            ? new(prepared.Outcome)
            : new(null, CalendarEntityCreateFidelity.PatchIntentDigest(prepared.AuthoritativeUtf8!, target));
    }

    private async Task<CalendarEntityPatchResult> MutateOccurrenceWithinDeadlineAsync(
        CalendarOccurrenceMutationRequest request,
        OccurrenceMutation operation,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareOccurrenceMutationAsync(request, operation, cancellationToken);
        if (prepared.Outcome is not null)
            return prepared.Outcome;
        return await DispatchAsync(
            request.Snapshot,
            prepared.AuthoritativeUtf8!,
            request.Snapshot.EntityKind,
            cancellationToken);
    }

    private async Task<PreparedPatch> PrepareOccurrenceMutationAsync(
        CalendarOccurrenceMutationRequest request,
        OccurrenceMutation operation,
        CancellationToken cancellationToken)
    {
        var revision = request.Snapshot;
        var currentRead = await ReadAsync(revision.Href, revision.EntityKind, cancellationToken);
        if (currentRead.Failure is not null)
            return new(null, currentRead.Failure);
        var current = currentRead.Read!;
        if (current.Snapshot is null)
            return new(null, FromReadFailure(current.Code));
        if (current.Snapshot.AuthoritativeUtf8.Length > MaximumAuthoritativeBytes)
        {
            return new(null, Failure(
                CalendarEntityPatchCode.PayloadTooLarge,
                current.Snapshot,
                phase: CalendarEntityPatchPhase.AdmissionAndPayload));
        }

        var validationFailure = ValidatePreparedOccurrence(revision, current.Snapshot);
        if (validationFailure is not null)
            return new(null, validationFailure);
        var edit = operation switch
        {
            OccurrenceMutation.Add => CalendarOccurrenceMembershipEditor.TryAdd(
                current.Snapshot,
                request.RecurrenceIdentity,
                revision.EntityKind,
                timeProvider.GetUtcNow(),
                cancellationToken),
            OccurrenceMutation.Exclude => CalendarOccurrenceMembershipEditor.TryExclude(
                current.Snapshot,
                request.RecurrenceIdentity,
                revision.EntityKind,
                timeProvider.GetUtcNow(),
                cancellationToken),
            OccurrenceMutation.RestoreExclusion => CalendarOccurrenceMembershipEditor.TryRestoreExclusion(
                current.Snapshot,
                request.RecurrenceIdentity,
                revision.EntityKind,
                timeProvider.GetUtcNow(),
                cancellationToken),
            OccurrenceMutation.Cancel => CalendarOccurrenceMembershipEditor.TryCancel(
                current.Snapshot,
                request.RecurrenceIdentity,
                revision.EntityKind,
                timeProvider.GetUtcNow(),
                cancellationToken),
            OccurrenceMutation.RestoreCancellation => CalendarOccurrenceMembershipEditor.TryRestoreCancellation(
                current.Snapshot,
                request.RecurrenceIdentity,
                revision.EntityKind,
                timeProvider.GetUtcNow(),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
        if (edit.Failure is not null)
            return new(null, edit.Failure);
        if (edit.AuthoritativeUtf8 is null)
        {
            return new(null, new CalendarEntityPatchResult(
                CalendarEntityPatchCode.NoChange,
                CalendarMutationState.NotAttempted,
                current.Snapshot,
                Phase: CalendarEntityPatchPhase.CompleteResourceSemantics));
        }
        if (edit.AuthoritativeUtf8.Length > MaximumAuthoritativeBytes)
        {
            return new(null, Failure(
                CalendarEntityPatchCode.PayloadTooLarge,
                current.Snapshot,
                phase: CalendarEntityPatchPhase.AdmissionAndPayload));
        }
        return new(edit.AuthoritativeUtf8, null);
    }

    private async Task<CalendarEntityPatchResult> CompleteTodoWithinDeadlineAsync(
        CalendarTodoCompletionRequest request,
        CancellationToken cancellationToken)
    {
        var currentRead = await ReadAsync(request.Snapshot.Href, CalendarEntityKind.Todo, cancellationToken);
        if (currentRead.Failure is not null)
            return currentRead.Failure;
        var current = currentRead.Read!;
        if (current.Snapshot is null)
            return FromReadFailure(current.Code);
        if (current.Snapshot.AuthoritativeUtf8.Length > MaximumAuthoritativeBytes)
        {
            return Failure(
                CalendarEntityPatchCode.PayloadTooLarge,
                current.Snapshot,
                phase: CalendarEntityPatchPhase.AdmissionAndPayload);
        }

        var validationFailure = ValidatePreparedOccurrence(request.Snapshot, current.Snapshot);
        if (validationFailure is not null)
            return validationFailure;
        var edit = CalendarTodoCompletionEditor.TryComplete(
            current.Snapshot,
            request.RecurrenceIdentity,
            timeProvider.GetUtcNow(),
            cancellationToken);
        if (edit.Failure is not null)
            return edit.Failure;
        if (edit.AuthoritativeUtf8 is null)
        {
            return new CalendarEntityPatchResult(
                CalendarEntityPatchCode.NoChange,
                CalendarMutationState.NotAttempted,
                current.Snapshot,
                Phase: CalendarEntityPatchPhase.CompleteResourceSemantics);
        }
        if (edit.AuthoritativeUtf8.Length > MaximumAuthoritativeBytes)
        {
            return Failure(
                CalendarEntityPatchCode.PayloadTooLarge,
                current.Snapshot,
                phase: CalendarEntityPatchPhase.AdmissionAndPayload);
        }
        return await DispatchAsync(
            request.Snapshot,
            edit.AuthoritativeUtf8,
            CalendarEntityKind.Todo,
            cancellationToken);
    }

    private async Task<PreparedPatch> PrepareAsync(
        CalendarResourceRevisionReference revision,
        CalendarMutationTarget target,
        CalendarEventPatch patch,
        CalendarEntityKind expectedKind,
        CancellationToken cancellationToken)
    {
        var currentRead = await ReadAsync(revision.Href, expectedKind, cancellationToken);
        if (currentRead.Failure is not null)
            return new(null, currentRead.Failure);
        var current = currentRead.Read!;
        if (current.Snapshot is null)
            return new(null, FromReadFailure(current.Code));
        if (current.Snapshot.AuthoritativeUtf8.Length > MaximumAuthoritativeBytes)
            return new(null, Failure(
                CalendarEntityPatchCode.PayloadTooLarge,
                current.Snapshot,
                phase: CalendarEntityPatchPhase.AdmissionAndPayload));
        var validationFailure = ValidatePreparedSnapshot(
            revision,
            current.Snapshot,
            target,
            patch,
            expectedKind);
        if (validationFailure is not null)
            return new(null, validationFailure);

        var edit = CalendarEntityPatchEditor.TryEdit(
            current.Snapshot,
            target,
            patch,
            expectedKind,
            timeProvider.GetUtcNow(),
            cancellationToken);
        if (edit.Failure is not null)
            return new(null, edit.Failure);
        if (edit.AuthoritativeUtf8 is null)
        {
            return new(null, new(
                CalendarEntityPatchCode.NoChange,
                CalendarMutationState.NotAttempted,
                current.Snapshot,
                Phase: CalendarEntityPatchPhase.CompleteResourceSemantics));
        }
        if (edit.AuthoritativeUtf8.Length > MaximumAuthoritativeBytes)
            return new(null, Failure(
                CalendarEntityPatchCode.PayloadTooLarge,
                current.Snapshot,
                phase: CalendarEntityPatchPhase.AdmissionAndPayload));
        return new(edit.AuthoritativeUtf8, null);
    }

    private static CalendarEventPatch EventPatch(CalendarTodoPatch patch) => new(
        Summary: patch.Summary,
        Description: patch.Description,
        Start: patch.Start,
        Due: patch.Due,
        Duration: patch.Duration,
        Status: patch.Status,
        Priority: patch.Priority,
        PercentComplete: patch.PercentComplete,
        Organizer: patch.Organizer,
        Categories: patch.Categories,
        Collections: patch.Collections,
        RecurrenceSet: patch.RecurrenceSet,
        RecurrenceSetAddressed: patch.RecurrenceSetAddressed,
        RequiresConfirmation: patch.RequiresConfirmation);

    private static CalendarEntityPatchReviewResult ReviewDeadlineFailure() => new(
        DeadlineFailure());

    private static CalendarEntityPatchResult DeadlineFailure() =>
        Failure(
            CalendarEntityPatchCode.LimitExhausted,
            phase: CalendarEntityPatchPhase.Execution,
            limitDimension: CalendarEntityPatchLimitDimension.ElapsedTime);

    private async Task<CalendarEntityPatchResult> DispatchAsync(
        CalendarResourceRevisionReference revision,
        byte[] authoritativeUtf8,
        CalendarEntityKind expectedKind,
        CancellationToken cancellationToken)
    {
        var dispatch = await calendarClient.UpdateCalendarResourceAsync(
            new CalendarResourceUpdateRequest(revision.Href, revision.EntityTag, authoritativeUtf8),
            cancellationToken);
        if (dispatch.Code == CalendarResourceUpdateDispatchCode.PossiblyDispatched)
            return await ReconcileAsync(revision, authoritativeUtf8, expectedKind);
        if (dispatch.Code == CalendarResourceUpdateDispatchCode.Conflict)
            return await RefreshConflictAsync(revision.Href, expectedKind, cancellationToken);
        if (dispatch.Code != CalendarResourceUpdateDispatchCode.Dispatched)
            return FromDispatchFailure(dispatch);
        return await VerifyAsync(revision, authoritativeUtf8, expectedKind);
    }

    private sealed record PreparedPatch(byte[]? AuthoritativeUtf8, CalendarEntityPatchResult? Outcome);

    private enum OccurrenceMutation
    {
        Add,
        Exclude,
        RestoreExclusion,
        Cancel,
        RestoreCancellation
    }

    private async Task<(CalendarResourceRead? Read, CalendarEntityPatchResult? Failure)> ReadAsync(
        string href,
        CalendarEntityKind expectedKind,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await readResource(href, expectedKind, cancellationToken), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, Failure(
                CalendarEntityPatchCode.UpstreamUnavailable,
                retryable: true,
                phase: CalendarEntityPatchPhase.SelectionDiscoveryCapability));
        }
        catch (HttpRequestException exception)
        {
            return (null, FromHttpFailure(exception.StatusCode));
        }
        catch (CalendarDiscoveryLimitException)
        {
            return (null, Failure(
                CalendarEntityPatchCode.LimitExhausted,
                phase: CalendarEntityPatchPhase.SelectionDiscoveryCapability));
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or CalendarDiscoveryProtocolException)
        {
            return (null, Failure(
                CalendarEntityPatchCode.UpstreamProtocolError,
                phase: CalendarEntityPatchPhase.SelectionDiscoveryCapability));
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return (null, Failure(
                CalendarEntityPatchCode.UnsupportedCapability,
                phase: CalendarEntityPatchPhase.SelectionDiscoveryCapability));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return (null, Failure(
                CalendarEntityPatchCode.UpstreamUnavailable,
                retryable: true,
                phase: CalendarEntityPatchPhase.SelectionDiscoveryCapability));
        }
    }

    private async Task<CalendarEntityPatchResult> VerifyAsync(
        CalendarResourceRevisionReference revision,
        byte[] intended,
        CalendarEntityKind expectedKind)
    {
        using var verification = new CancellationTokenSource(ReconciliationTimeout, timeProvider);
        try
        {
            var observed = await readResource(revision.Href, expectedKind, verification.Token);
            if (MatchesIntended(observed, intended, expectedKind))
            {
                return IsSameRevision(revision, observed.Snapshot!)
                    ? PostWrite(CalendarEntityPatchCode.CommittedButConcurrencyUnavailable, CalendarMutationState.Committed)
                    : CalendarEntityPatchResult.Success(observed.Snapshot!);
            }
            if (observed.Code == CalendarResourceReadCode.ConcurrencyUnavailable)
            {
                if (HasUnversionedCommitEvidence(observed, intended, expectedKind))
                    return PostWrite(CalendarEntityPatchCode.CommittedButConcurrencyUnavailable, CalendarMutationState.Committed);
                return HasUsableUnversionedProjection(observed)
                    ? PostWrite(CalendarEntityPatchCode.FidelityFailure, CalendarMutationState.Committed)
                    : PostWrite(CalendarEntityPatchCode.CommittedButUnverified, CalendarMutationState.Committed);
            }
            return observed.Snapshot is null
                ? PostWrite(CalendarEntityPatchCode.CommittedButUnverified, CalendarMutationState.Committed)
                : PostWrite(CalendarEntityPatchCode.FidelityFailure, CalendarMutationState.Committed, observed.Snapshot);
        }
        catch (Exception)
        {
            return PostWrite(CalendarEntityPatchCode.CommittedButUnverified, CalendarMutationState.Committed);
        }
    }

    private async Task<CalendarEntityPatchResult> ReconcileAsync(
        CalendarResourceRevisionReference revision,
        byte[] intended,
        CalendarEntityKind expectedKind)
    {
        using var reconciliation = new CancellationTokenSource(ReconciliationTimeout, timeProvider);
        try
        {
            var observed = await readResource(revision.Href, expectedKind, reconciliation.Token);
            if (MatchesIntended(observed, intended, expectedKind))
            {
                return IsSameRevision(revision, observed.Snapshot!)
                    ? PostWrite(CalendarEntityPatchCode.CommittedButConcurrencyUnavailable, CalendarMutationState.Committed)
                    : CalendarEntityPatchResult.Success(observed.Snapshot!);
            }
            if (observed.Code == CalendarResourceReadCode.ConcurrencyUnavailable
                && HasUnversionedCommitEvidence(observed, intended, expectedKind))
            {
                return PostWrite(
                    CalendarEntityPatchCode.CommittedButConcurrencyUnavailable,
                    CalendarMutationState.Committed);
            }
            if (observed.Snapshot is not null && IsSameRevision(revision, observed.Snapshot))
                return PostWrite(
                    CalendarEntityPatchCode.UpstreamUnavailable,
                    CalendarMutationState.NotCommitted,
                    observed.Snapshot,
                    retryable: true);
            return PostWrite(CalendarEntityPatchCode.Indeterminate, CalendarMutationState.Unknown, observed.Snapshot);
        }
        catch (Exception)
        {
            return PostWrite(CalendarEntityPatchCode.Indeterminate, CalendarMutationState.Unknown);
        }
    }

    private async Task<CalendarEntityPatchResult> RefreshConflictAsync(
        string href,
        CalendarEntityKind expectedKind,
        CancellationToken cancellationToken)
    {
        try
        {
            var observed = await readResource(href, expectedKind, cancellationToken);
            return Rejected(
                CalendarEntityPatchCode.Conflict,
                observed.Snapshot,
                CalendarEntityPatchPhase.TargetRevision);
        }
        catch (Exception)
        {
            return Rejected(CalendarEntityPatchCode.Conflict, phase: CalendarEntityPatchPhase.TargetRevision);
        }
    }

    private static bool MatchesIntended(
        CalendarResourceRead observed,
        ReadOnlySpan<byte> intended,
        CalendarEntityKind expectedKind)
    {
        if (observed.Code != CalendarResourceReadCode.Success || observed.Snapshot is null)
            return false;
        var kind = observed.Snapshot.Projection.Kind switch
        {
            CalendarResourceProjectionKind.Event => CalendarEntityKind.Event,
            CalendarResourceProjectionKind.Todo => CalendarEntityKind.Todo,
            _ => (CalendarEntityKind?)null
        };
        return kind == expectedKind && CalendarEntityCreateFidelity.IsPatchEquivalent(
            intended,
            observed.Snapshot.AuthoritativeUtf8.Span);
    }

    private static bool HasUnversionedCommitEvidence(
        CalendarResourceRead observed,
        ReadOnlySpan<byte> intended,
        CalendarEntityKind expectedKind)
    {
        if (observed.Code != CalendarResourceReadCode.ConcurrencyUnavailable
            || observed.AuthoritativeUtf8.IsEmpty)
            return false;
        var projection = CalendarResourceProjector.Project(observed.AuthoritativeUtf8.Span).Projection.Kind;
        var kindMatches = projection switch
        {
            CalendarResourceProjectionKind.Event => expectedKind == CalendarEntityKind.Event,
            CalendarResourceProjectionKind.Todo => expectedKind == CalendarEntityKind.Todo,
            _ => false
        };
        return kindMatches && CalendarEntityCreateFidelity.IsPatchEquivalent(
            intended,
            observed.AuthoritativeUtf8.Span);
    }

    private static bool HasUsableUnversionedProjection(CalendarResourceRead observed)
    {
        if (observed.Code != CalendarResourceReadCode.ConcurrencyUnavailable
            || observed.AuthoritativeUtf8.IsEmpty
            || observed.AuthoritativeUtf8.Length > MaximumAuthoritativeBytes)
            return false;
        try
        {
            return CalendarResourceProjector.Project(observed.AuthoritativeUtf8.Span).Projection.Kind
                is CalendarResourceProjectionKind.Event or CalendarResourceProjectionKind.Todo;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static CalendarEntityPatchCode? ValidateRevisionShape(
        CalendarResourceRevisionReference revision,
        CalendarEntityKind expectedKind)
    {
        if (!HasValidRevisionShape(revision, expectedKind))
            return CalendarEntityPatchCode.InvalidInput;
        _ = EntityTagHeaderValue.TryParse(revision.EntityTag, out var tag);
        return tag!.IsWeak ? CalendarEntityPatchCode.ConcurrencyUnavailable : null;
    }

    private static CalendarEntityPatchResult? ValidatePreparedSnapshot(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot snapshot,
        CalendarMutationTarget target,
        CalendarEventPatch patch,
        CalendarEntityKind expectedKind)
    {
        var revisionShapeFailure = ValidateRevisionShape(revision, expectedKind);
        if (revisionShapeFailure is not null)
            return Failure(revisionShapeFailure.Value, phase: CalendarEntityPatchPhase.TargetRevision);
        return ValidateAuthoritativeRevision(revision, snapshot)
            ?? ValidateProjection(snapshot, expectedKind)
            ?? (!HasValidPatchShape(target, patch, expectedKind)
                ? Failure(CalendarEntityPatchCode.InvalidInput)
                : null);
    }

    private static CalendarEntityPatchResult? ValidatePreparedOccurrence(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot snapshot)
    {
        var revisionShapeFailure = ValidateRevisionShape(revision, revision.EntityKind);
        return revisionShapeFailure is not null
            ? Failure(revisionShapeFailure.Value, phase: CalendarEntityPatchPhase.TargetRevision)
            : ValidateAuthoritativeRevision(revision, snapshot)
                ?? ValidateProjection(snapshot, revision.EntityKind);
    }

    private static bool HasValidRevisionShape(
        CalendarResourceRevisionReference revision,
        CalendarEntityKind expectedKind) => revision.EntityKind == expectedKind
        && !string.IsNullOrEmpty(revision.EntityUid)
        && EntityTagHeaderValue.TryParse(revision.EntityTag, out var tag)
        && tag is not null
        && tag != EntityTagHeaderValue.Any
        && string.Equals(tag.ToString(), revision.EntityTag, StringComparison.Ordinal);

    private static bool HasValidPatchShape(
        CalendarMutationTarget target,
        CalendarEventPatch patch,
        CalendarEntityKind expectedKind) =>
        IsValidTarget(target)
        && patch.RecurrenceSetAddressed == (patch.RecurrenceSet is not null)
        && !patch.RequiresConfirmation
        && HasPatchIntent(patch)
        && HasValidScalars(patch)
        && HasValidScalarValues(patch, expectedKind)
        && HasValidCategoryPatch(patch.Categories)
        && HasValidStructuredCollections(patch.Collections)
        && HasValidStructuredValues(patch.Collections, expectedKind);

    private static bool IsValidTarget(CalendarMutationTarget target) => target.Scope switch
    {
        "master" or "entire-set" => target.RecurrenceIdentity is null,
        "one-occurrence" or "this-and-future" => target.RecurrenceIdentity is not null,
        _ => false
    };

    private static bool HasValidScalarValues(CalendarEventPatch patch, CalendarEntityKind expectedKind)
    {
        try
        {
            CalendarEntityCreateValidator.ValidatePatchScalars(patch, expectedKind);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasValidStructuredValues(
        IReadOnlyList<ICalendarCollectionPatch>? patches,
        CalendarEntityKind expectedKind)
    {
        try
        {
            foreach (var patch in patches ?? [])
            foreach (var value in (patch.AddValues ?? []).Concat(patch.RemoveValues ?? [])
                         .Concat(patch.ReplacementValues ?? []))
                _ = CalendarPatchOccurrenceSerializer.Serialize(patch.Field, value, expectedKind);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidCastException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasPatchIntent(CalendarEventPatch patch) => new object?[]
    {
        patch.Summary, patch.Description, patch.Start, patch.End, patch.Due, patch.Duration, patch.Location,
        patch.Geo, patch.Status, patch.Transparency, patch.Classification, patch.Priority, patch.PercentComplete,
        patch.Url, patch.Organizer, patch.Categories, patch.Collections, patch.RecurrenceSet
    }.Any(value => value is not null);

    private static bool HasValidScalars(CalendarEventPatch patch) => new[]
    {
        IsValidScalar(patch.Summary), IsValidScalar(patch.Description), IsValidScalar(patch.Start),
        IsValidScalar(patch.End), IsValidScalar(patch.Due), IsValidScalar(patch.Duration),
        IsValidScalar(patch.Location), IsValidScalar(patch.Geo), IsValidScalar(patch.Status),
        IsValidScalar(patch.Transparency), IsValidScalar(patch.Classification), IsValidScalar(patch.Priority),
        IsValidScalar(patch.PercentComplete), IsValidScalar(patch.Url),
        IsValidScalar(patch.Organizer)
    }.All(valid => valid);

    private static bool IsValidScalar<T>(CalendarScalarPatch<T>? patch) => patch is null
        || Enum.IsDefined(patch.Operation)
        && (patch.Operation != CalendarScalarPatchOperation.Set || patch.Value is not null);

    private static bool HasValidCategoryPatch(CalendarCollectionPatch<string>? patch)
    {
        if (patch is null || !Enum.IsDefined(patch.Operation))
            return patch is null;
        return patch.Operation == CalendarCollectionPatchOperation.ReplaceAll
            ? HasValidReplaceAll(patch)
            : HasValidAddRemove(patch);
    }

    private static bool HasValidReplaceAll(CalendarCollectionPatch<string> patch) => patch.Values is not null
        && patch.Add is null
        && patch.Remove is null
        && patch.Values.All(IsValidCategory);

    private static bool HasValidAddRemove(CalendarCollectionPatch<string> patch) => patch.Values is null
        && HasAddOrRemove(patch)
        && (patch.Add ?? []).All(IsValidCategory)
        && (patch.Remove ?? []).All(IsValidCategory);

    private static bool HasAddOrRemove(CalendarCollectionPatch<string> patch) =>
        (patch.Add?.Count ?? 0) > 0 || (patch.Remove?.Count ?? 0) > 0;

    private static bool IsValidCategory(string value) => !string.IsNullOrEmpty(value)
        && value.IndexOfAny(['\r', '\n']) < 0;

    private static bool HasValidStructuredCollections(IReadOnlyList<ICalendarCollectionPatch>? patches)
    {
        if (patches is null)
            return true;
        return patches.Count > 0
            && patches.All(IsValidStructuredCollection)
            && patches.Select(patch => patch.Field).Distinct().Count() == patches.Count;
    }

    private static bool IsValidStructuredCollection(ICalendarCollectionPatch patch)
    {
        if (patch.Field == CalendarCollectionField.Categories || !Enum.IsDefined(patch.Operation))
            return false;
        return patch.Operation == CalendarCollectionPatchOperation.ReplaceAll
            ? IsValidStructuredReplaceAll(patch)
            : IsValidStructuredAddRemove(patch);
    }

    private static bool IsValidStructuredReplaceAll(ICalendarCollectionPatch patch) =>
        patch.ReplacementValues is not null && patch.AddValues is null && patch.RemoveValues is null;

    private static bool IsValidStructuredAddRemove(ICalendarCollectionPatch patch) =>
        patch.ReplacementValues is null && HasStructuredAddOrRemove(patch);

    private static bool HasStructuredAddOrRemove(ICalendarCollectionPatch patch) =>
        (patch.AddValues?.Count ?? 0) > 0 || (patch.RemoveValues?.Count ?? 0) > 0;

    private static CalendarEntityPatchResult? ValidateAuthoritativeRevision(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot snapshot)
    {
        if (!string.Equals(snapshot.EntityTag, revision.EntityTag, StringComparison.Ordinal)
            || snapshot.Projection.EntityUid is not null
            && !string.Equals(snapshot.Projection.EntityUid, revision.EntityUid, StringComparison.Ordinal))
        {
            return Failure(
                CalendarEntityPatchCode.Conflict,
                snapshot,
                phase: CalendarEntityPatchPhase.TargetRevision);
        }
        return null;
    }

    private static CalendarEntityPatchResult? ValidateProjection(
        CalendarResourceSnapshot snapshot,
        CalendarEntityKind expectedKind)
    {
        if (!snapshot.SemanticMutationAvailable)
            return Failure(CalendarEntityPatchCode.OpaqueResource, snapshot);
        var kind = snapshot.Projection.Kind == CalendarResourceProjectionKind.Event
            ? CalendarEntityKind.Event
            : CalendarEntityKind.Todo;
        if (kind != expectedKind)
            return Failure(CalendarEntityPatchCode.EntityKindMismatch, snapshot);
        return null;
    }

    private static bool IsSameRevision(CalendarResourceRevisionReference revision, CalendarResourceSnapshot snapshot) =>
        string.Equals(snapshot.ResourceHref, revision.Href, StringComparison.Ordinal)
        && string.Equals(snapshot.Projection.EntityUid, revision.EntityUid, StringComparison.Ordinal)
        && string.Equals(snapshot.EntityTag, revision.EntityTag, StringComparison.Ordinal);

    private static bool PathsEqual(
        IReadOnlyList<CalendarComponentPathSegment> left,
        IReadOnlyList<CalendarComponentPathSegment> right) => left.Count == right.Count
        && left.Zip(right).All(pair => pair.First == pair.Second);

    private static CalendarEntityPatchResult FromReadFailure(CalendarResourceReadCode code) => code switch
    {
        CalendarResourceReadCode.InvalidInput => Failure(CalendarEntityPatchCode.InvalidInput),
        CalendarResourceReadCode.NotFound => Failure(
            CalendarEntityPatchCode.NotFound,
            phase: CalendarEntityPatchPhase.SelectionDiscoveryCapability),
        CalendarResourceReadCode.OutsideScope => Failure(
            CalendarEntityPatchCode.OutsideScope,
            phase: CalendarEntityPatchPhase.OriginScopeAuthorization),
        CalendarResourceReadCode.ConcurrencyUnavailable => Failure(
            CalendarEntityPatchCode.ConcurrencyUnavailable,
            phase: CalendarEntityPatchPhase.TargetRevision),
        CalendarResourceReadCode.PayloadTooLarge => Failure(
            CalendarEntityPatchCode.PayloadTooLarge,
            phase: CalendarEntityPatchPhase.AdmissionAndPayload),
        CalendarResourceReadCode.UnsupportedCapability => Failure(
            CalendarEntityPatchCode.UnsupportedCapability,
            phase: CalendarEntityPatchPhase.SelectionDiscoveryCapability),
        _ => Failure(
            CalendarEntityPatchCode.UpstreamProtocolError,
            phase: CalendarEntityPatchPhase.SelectionDiscoveryCapability)
    };

    private static CalendarEntityPatchResult FromHttpFailure(System.Net.HttpStatusCode? statusCode) => statusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized => DiscoveryFailure(CalendarEntityPatchCode.UpstreamUnauthorized),
        System.Net.HttpStatusCode.Forbidden => DiscoveryFailure(CalendarEntityPatchCode.UpstreamForbidden),
        System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed =>
            DiscoveryFailure(CalendarEntityPatchCode.Conflict),
        System.Net.HttpStatusCode.RequestEntityTooLarge => DiscoveryFailure(CalendarEntityPatchCode.PayloadTooLarge),
        System.Net.HttpStatusCode.TooManyRequests => DiscoveryFailure(CalendarEntityPatchCode.UpstreamRateLimited, retryable: true),
        System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented =>
            DiscoveryFailure(CalendarEntityPatchCode.UnsupportedCapability),
        System.Net.HttpStatusCode.NotFound => DiscoveryFailure(CalendarEntityPatchCode.UpstreamProtocolError),
        (System.Net.HttpStatusCode)507 => DiscoveryFailure(CalendarEntityPatchCode.UpstreamUnavailable),
        >= System.Net.HttpStatusCode.InternalServerError => DiscoveryFailure(CalendarEntityPatchCode.UpstreamUnavailable, retryable: true),
        _ => DiscoveryFailure(CalendarEntityPatchCode.UpstreamUnavailable)
    };

    private static CalendarEntityPatchResult DiscoveryFailure(
        CalendarEntityPatchCode code,
        bool retryable = false) => Failure(
        code,
        retryable: retryable,
        phase: CalendarEntityPatchPhase.SelectionDiscoveryCapability);

    private static CalendarEntityPatchResult FromDispatchFailure(CalendarResourceUpdateDispatchResult dispatch) => dispatch.Code switch
    {
        CalendarResourceUpdateDispatchCode.NotFound => Rejected(CalendarEntityPatchCode.NotFound),
        CalendarResourceUpdateDispatchCode.InvalidInput => Rejected(CalendarEntityPatchCode.InvalidInput),
        CalendarResourceUpdateDispatchCode.UnsupportedCapability => Rejected(CalendarEntityPatchCode.UnsupportedCapability),
        CalendarResourceUpdateDispatchCode.PayloadTooLarge => Rejected(CalendarEntityPatchCode.PayloadTooLarge),
        CalendarResourceUpdateDispatchCode.UpstreamUnauthorized => Rejected(CalendarEntityPatchCode.UpstreamUnauthorized),
        CalendarResourceUpdateDispatchCode.UpstreamForbidden => Rejected(CalendarEntityPatchCode.UpstreamForbidden),
        CalendarResourceUpdateDispatchCode.UpstreamRateLimited => Rejected(
            CalendarEntityPatchCode.UpstreamRateLimited,
            dispatch.RetryAfterMilliseconds,
            retryable: true),
        CalendarResourceUpdateDispatchCode.UpstreamProtocolError => Rejected(CalendarEntityPatchCode.UpstreamProtocolError),
        _ => Rejected(CalendarEntityPatchCode.UpstreamUnavailable)
    };

    private static CalendarEntityPatchResult Rejected(
        CalendarEntityPatchCode code,
        int? retryAfterMilliseconds = null,
        bool retryable = false,
        CalendarEntityPatchPhase phase = CalendarEntityPatchPhase.Execution) => new(
        code,
        CalendarMutationState.NotCommitted,
        RetryAfterMilliseconds: retryAfterMilliseconds,
        Retryable: retryable,
        Phase: phase);

    private static CalendarEntityPatchResult Rejected(
        CalendarEntityPatchCode code,
        CalendarResourceSnapshot? snapshot,
        CalendarEntityPatchPhase phase = CalendarEntityPatchPhase.Execution) => new(
        code,
        CalendarMutationState.NotCommitted,
        snapshot,
        Phase: phase);

    private static CalendarEntityPatchResult Failure(
        CalendarEntityPatchCode code,
        CalendarResourceSnapshot? snapshot = null,
        bool retryable = false,
        CalendarEntityPatchPhase phase = CalendarEntityPatchPhase.CompleteResourceSemantics,
        CalendarEntityPatchLimitDimension? limitDimension = null) => new(
        code,
        CalendarMutationState.NotAttempted,
        snapshot,
        Retryable: retryable,
        Phase: phase,
        LimitDimension: limitDimension);

    private static CalendarEntityPatchResult PostWrite(
        CalendarEntityPatchCode code,
        CalendarMutationState mutationState,
        CalendarResourceSnapshot? snapshot = null,
        bool retryable = false) => new(
        code,
        mutationState,
        snapshot,
        Retryable: retryable,
        Phase: CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation);
}
