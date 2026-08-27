using System.Diagnostics;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Mcp.Hosting;

internal static class CalendarTelemetry
{
    internal const string InstrumentationName = "DotnetAgents.CalDav";
    internal const string InstrumentationVersion = "0.1.0";
    private static readonly ActivitySource Source = new(InstrumentationName, InstrumentationVersion);
    private static readonly AsyncLocal<CalendarTelemetryOperation?> CurrentOperation = new();

    internal static CalendarTelemetryOperation? StartOperation(
        string toolName,
        CalendarTelemetryEntityKind? entityKind)
    {
        if (!Source.HasListeners())
            return null;

        var activity = Source.StartActivity("caldav.operation", ActivityKind.Internal);
        if (activity is null)
            return null;

        if (activity.IsAllDataRequested)
        {
            activity.SetTag("caldav.tool.name", toolName);
            if (entityKind is not null)
                activity.SetTag("caldav.entity.kind", EntityKindName(entityKind.Value));
        }

        return new CalendarTelemetryOperation(Source, activity);
    }

    internal static string NormalizeToolName(string? toolName) => toolName switch
    {
        "calendars.list" or "calendars.create" or "calendars.delete" or "calendar_entities.query" or "calendar_occurrences.query"
            or "todos.query" or "calendar_resources.get" or "events.create" or "events.patch"
            or "todos.create" or "todos.patch" or "todos.complete" or "calendar_occurrences.add"
            or "calendar_occurrences.exclude" or "calendar_occurrences.restore_exclusion"
            or "calendar_occurrences.cancel" or "calendar_occurrences.restore_cancellation"
            or "calendar_resources.move" or "calendar_resources.delete"
            or "calendar_resources.exact_get" or "calendar_resources.exact_create"
            or "calendar_resources.exact_replace" or "calendar_resources.exact_move" => toolName,
        _ => "unknown"
    };

    internal static IDisposable Attach(CalendarTelemetryOperation? operation)
    {
        var previous = CurrentOperation.Value;
        CurrentOperation.Value = operation;
        return new OperationScope(previous, operation);
    }

    internal static void ObserveStructuredError(CalendarStructuredErrorFacts facts) =>
        CurrentOperation.Value?.ObserveStructuredError(facts);

    internal static void ObserveMutationState(CalendarMutationState mutationState) =>
        CurrentOperation.Value?.ObserveMutationState(mutationState);

    internal static void ObserveStructuredError(
        CalendarStructuredErrorFacts facts,
        CalendarMutationState mutationState)
    {
        ObserveStructuredError(facts);
        ObserveMutationState(mutationState);
    }

    private static string EntityKindName(CalendarTelemetryEntityKind entityKind) => entityKind switch
    {
        CalendarTelemetryEntityKind.Event => "event",
        CalendarTelemetryEntityKind.Todo => "todo",
        _ => throw new ArgumentOutOfRangeException(nameof(entityKind), entityKind, null)
    };

    private sealed class OperationScope(
        CalendarTelemetryOperation? previous,
        CalendarTelemetryOperation? operation) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(CurrentOperation.Value, operation))
                CurrentOperation.Value = previous;
        }
    }
}

internal enum CalendarTelemetryEntityKind
{
    Event,
    Todo
}

internal enum CalendarOperationOutcome
{
    Success,
    InputRequired,
    Cancelled,
    Error
}

internal enum CalendarTelemetryErrorCode
{
    Ambiguous,
    Busy,
    CommittedButConcurrencyUnavailable,
    CommittedButUnverified,
    CompletionStateConflict,
    ConcurrencyUnavailable,
    ConfirmationExpired,
    CursorExpired,
    ConfirmationMismatch,
    Conflict,
    DestinationConflict,
    EntityKindMismatch,
    FidelityFailure,
    Indeterminate,
    InvalidCalendarData,
    InvalidInput,
    LimitExhausted,
    NotFound,
    OpaqueResource,
    OutsideScope,
    PayloadTooLarge,
    RecurrenceUnevaluable,
    TemporalUnresolved,
    UnsupportedCapability,
    UpstreamForbidden,
    UpstreamProtocolError,
    UpstreamRateLimited,
    UpstreamUnauthorized,
    UpstreamUnavailable
}

internal enum CalendarTelemetryErrorCategory
{
    CapabilityAndProjection,
    Confirmation,
    Input,
    LimitsAndAdmission,
    PostWriteTruth,
    Selection,
    State,
    Upstream
}

internal enum CalendarTelemetryErrorPhase
{
    AdmissionAndPayload,
    CompleteResourceSemantics,
    Execution,
    Mrtr,
    OriginScopeAuthorization,
    Pagination,
    PostWriteVerificationOrReconciliation,
    SchemaLexicalDiscriminator,
    SelectionDiscoveryCapability,
    TargetRevision,
    TransportAuthorization
}

internal readonly record struct CalendarStructuredErrorFacts(
    CalendarTelemetryErrorCode Code,
    CalendarTelemetryErrorCategory Category,
    CalendarTelemetryErrorPhase Phase,
    bool Retryable)
{
    internal string CodeName => CalendarTelemetryVocabulary.ErrorCodeName(Code);

    internal string CategoryName => CalendarTelemetryVocabulary.ErrorCategoryName(Category);

    internal string PhaseName => CalendarTelemetryVocabulary.ErrorPhaseName(Phase);
}

internal static class CalendarTelemetryVocabulary
{
    private static readonly HashSet<string> ErrorCodes = new(StringComparer.Ordinal)
    {
        "ambiguous", "busy", "committed_but_concurrency_unavailable", "committed_but_unverified",
        "completion_state_conflict", "concurrency_unavailable", "confirmation_expired", "cursor_expired",
        "confirmation_mismatch", "conflict", "destination_conflict", "entity_kind_mismatch",
        "fidelity_failure", "indeterminate", "invalid_calendar_data", "invalid_input",
        "limit_exhausted", "not_found", "opaque_resource", "outside_scope", "payload_too_large",
        "recurrence_unevaluable", "temporal_unresolved", "unsupported_capability", "upstream_forbidden",
        "upstream_protocol_error", "upstream_rate_limited", "upstream_unauthorized", "upstream_unavailable"
    };
    private static readonly HashSet<string> ErrorCategories = new(StringComparer.Ordinal)
    {
        "capabilityAndProjection", "confirmation", "input", "limitsAndAdmission", "postWriteTruth",
        "selection", "state", "upstream"
    };
    private static readonly HashSet<string> ErrorPhases = new(StringComparer.Ordinal)
    {
        "admissionAndPayload", "completeResourceSemantics", "execution", "mrtr",
        "originScopeAuthorization", "pagination", "postWriteVerificationOrReconciliation",
        "schemaLexicalDiscriminator", "selectionDiscoveryCapability", "targetRevision",
        "transportAuthorization"
    };

    internal static string? ErrorCode(string? value) => Known(value, ErrorCodes);

    internal static IReadOnlySet<string> KnownErrorCodes => ErrorCodes;

    internal static string? ErrorCategory(string? value) => Known(value, ErrorCategories);

    internal static IReadOnlySet<string> KnownErrorCategories => ErrorCategories;

    internal static string? ErrorPhase(string? value) => Known(value, ErrorPhases);

    internal static IReadOnlySet<string> KnownErrorPhases => ErrorPhases;

    internal static string ErrorCodeName(CalendarTelemetryErrorCode code) => code switch
    {
        CalendarTelemetryErrorCode.Ambiguous => "ambiguous",
        CalendarTelemetryErrorCode.Busy => "busy",
        CalendarTelemetryErrorCode.CommittedButConcurrencyUnavailable => "committed_but_concurrency_unavailable",
        CalendarTelemetryErrorCode.CommittedButUnverified => "committed_but_unverified",
        CalendarTelemetryErrorCode.CompletionStateConflict => "completion_state_conflict",
        CalendarTelemetryErrorCode.ConcurrencyUnavailable => "concurrency_unavailable",
        CalendarTelemetryErrorCode.ConfirmationExpired => "confirmation_expired",
        CalendarTelemetryErrorCode.CursorExpired => "cursor_expired",
        CalendarTelemetryErrorCode.ConfirmationMismatch => "confirmation_mismatch",
        CalendarTelemetryErrorCode.Conflict => "conflict",
        CalendarTelemetryErrorCode.DestinationConflict => "destination_conflict",
        CalendarTelemetryErrorCode.EntityKindMismatch => "entity_kind_mismatch",
        CalendarTelemetryErrorCode.FidelityFailure => "fidelity_failure",
        CalendarTelemetryErrorCode.Indeterminate => "indeterminate",
        CalendarTelemetryErrorCode.InvalidCalendarData => "invalid_calendar_data",
        CalendarTelemetryErrorCode.InvalidInput => "invalid_input",
        CalendarTelemetryErrorCode.LimitExhausted => "limit_exhausted",
        CalendarTelemetryErrorCode.NotFound => "not_found",
        CalendarTelemetryErrorCode.OpaqueResource => "opaque_resource",
        CalendarTelemetryErrorCode.OutsideScope => "outside_scope",
        CalendarTelemetryErrorCode.PayloadTooLarge => "payload_too_large",
        CalendarTelemetryErrorCode.RecurrenceUnevaluable => "recurrence_unevaluable",
        CalendarTelemetryErrorCode.TemporalUnresolved => "temporal_unresolved",
        CalendarTelemetryErrorCode.UnsupportedCapability => "unsupported_capability",
        CalendarTelemetryErrorCode.UpstreamForbidden => "upstream_forbidden",
        CalendarTelemetryErrorCode.UpstreamProtocolError => "upstream_protocol_error",
        CalendarTelemetryErrorCode.UpstreamRateLimited => "upstream_rate_limited",
        CalendarTelemetryErrorCode.UpstreamUnauthorized => "upstream_unauthorized",
        CalendarTelemetryErrorCode.UpstreamUnavailable => "upstream_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
    };

    internal static string ErrorCategoryName(CalendarTelemetryErrorCategory category) => category switch
    {
        CalendarTelemetryErrorCategory.CapabilityAndProjection => "capabilityAndProjection",
        CalendarTelemetryErrorCategory.Confirmation => "confirmation",
        CalendarTelemetryErrorCategory.Input => "input",
        CalendarTelemetryErrorCategory.LimitsAndAdmission => "limitsAndAdmission",
        CalendarTelemetryErrorCategory.PostWriteTruth => "postWriteTruth",
        CalendarTelemetryErrorCategory.Selection => "selection",
        CalendarTelemetryErrorCategory.State => "state",
        CalendarTelemetryErrorCategory.Upstream => "upstream",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    internal static string ErrorPhaseName(CalendarTelemetryErrorPhase phase) => phase switch
    {
        CalendarTelemetryErrorPhase.AdmissionAndPayload => "admissionAndPayload",
        CalendarTelemetryErrorPhase.CompleteResourceSemantics => "completeResourceSemantics",
        CalendarTelemetryErrorPhase.Execution => "execution",
        CalendarTelemetryErrorPhase.Mrtr => "mrtr",
        CalendarTelemetryErrorPhase.OriginScopeAuthorization => "originScopeAuthorization",
        CalendarTelemetryErrorPhase.Pagination => "pagination",
        CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation =>
            "postWriteVerificationOrReconciliation",
        CalendarTelemetryErrorPhase.SchemaLexicalDiscriminator => "schemaLexicalDiscriminator",
        CalendarTelemetryErrorPhase.SelectionDiscoveryCapability => "selectionDiscoveryCapability",
        CalendarTelemetryErrorPhase.TargetRevision => "targetRevision",
        CalendarTelemetryErrorPhase.TransportAuthorization => "transportAuthorization",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    internal static string MutationStateName(CalendarMutationState state) => state switch
    {
        CalendarMutationState.NotAttempted => "not_attempted",
        CalendarMutationState.NotCommitted => "not_committed",
        CalendarMutationState.Committed => "committed",
        CalendarMutationState.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    internal static string? MoveDispatch(string? value) => value switch
    {
        "not_attempted" or "rejected" or "dispatched" or "possibly_dispatched" => value,
        _ => null
    };

    internal static string? MoveCollision(string? value) => value switch
    {
        "none" or "source_revision" or "destination_href" or "uid" or "unclassified" => value,
        _ => null
    };

    internal static string? MoveReconciliation(string? value) => value switch
    {
        "not_run" or "faithful_destination_source_absent" or "divergent_destination_source_absent"
            or "observation_unavailable" or "unchanged_source_destination_absent" or "indeterminate" => value,
        _ => null
    };

    internal static string? ErrorType(object? value)
    {
        if (value is not string text)
            return null;
        if (text is "timeout" or "connection_error" or "response_ended" or "protocol_error" or "internal_error")
            return text;
        if (text.StartsWith("caldav.", StringComparison.Ordinal)
            && ErrorCode(text["caldav.".Length..]) is not null)
        {
            return text;
        }
        return text is { Length: 3 } && text.All(char.IsAsciiDigit) ? text : null;
    }

    private static string? Known(string? value, HashSet<string> known) =>
        value is not null && known.Contains(value) ? value : null;
}

internal sealed class CalendarTelemetryOperation : IDisposable
{
    private readonly ActivitySource _source;
    private readonly Activity _operation;
    private Activity? _phase;
    private CalendarStructuredErrorFacts? _structuredError;
    private CalendarMutationState? _mutationState;

    internal CalendarTelemetryOperation(ActivitySource source, Activity operation)
    {
        _source = source;
        _operation = operation;
    }

    internal void StartPhase(CalendarOperationPhase phase)
    {
        _phase?.Stop();
        _phase = _source.StartActivity(PhaseActivityName(phase), ActivityKind.Internal);
        if (_phase?.IsAllDataRequested == true)
            _phase.SetTag("caldav.phase", PhaseName(phase));
    }

    internal void ObserveStructuredError(CalendarStructuredErrorFacts facts) => _structuredError = facts;

    internal void ObserveMutationState(CalendarMutationState mutationState) => _mutationState = mutationState;

    internal void ObserveMutationStateIfAbsent(CalendarMutationState mutationState) =>
        _mutationState ??= mutationState;

    internal void Complete(
        CalendarOperationOutcome outcome,
        CalendarMoveTelemetrySnapshot? moveTelemetry = null)
    {
        if (!_operation.IsAllDataRequested)
            return;

        var outcomeName = OutcomeName(outcome);
        _operation.SetTag("caldav.outcome", outcomeName);
        if (outcome != CalendarOperationOutcome.Success)
            _operation.SetTag("caldav.transport.recovered", null);
        _operation.SetTag(
            "caldav.mutation.state",
            _mutationState is { } mutationState
                ? CalendarTelemetryVocabulary.MutationStateName(mutationState)
                : null);
        _operation.SetTag("caldav.move.dispatch", MoveDispatch(moveTelemetry));
        _operation.SetTag("caldav.move.collision", MoveCollision(moveTelemetry));
        _operation.SetTag("caldav.move.reconciliation", MoveReconciliation(moveTelemetry));
        if (outcome == CalendarOperationOutcome.Error && _structuredError is { } error)
        {
            var errorCode = CalendarTelemetryVocabulary.ErrorCodeName(error.Code);
            _operation.SetTag("caldav.error.code", errorCode);
            _operation.SetTag(
                "caldav.error.category",
                CalendarTelemetryVocabulary.ErrorCategoryName(error.Category));
            _operation.SetTag(
                "caldav.error.phase",
                CalendarTelemetryVocabulary.ErrorPhaseName(error.Phase));
            _operation.SetTag("caldav.error.retryable", error.Retryable);
            _operation.SetTag("error.type", $"caldav.{errorCode}");
            _operation.SetStatus(ActivityStatusCode.Error);
        }
        else if (outcome == CalendarOperationOutcome.Error)
        {
            _operation.SetStatus(ActivityStatusCode.Error);
        }
    }

    internal void Fail(Exception exception)
    {
        if (!_operation.IsAllDataRequested)
            return;

        _operation.SetTag("caldav.outcome", OutcomeName(CalendarOperationOutcome.Error));
        _operation.SetTag("error.type", ClassifyException(exception));
        _operation.SetStatus(ActivityStatusCode.Error);
    }

    public void Dispose()
    {
        _phase?.Dispose();
        _operation.Dispose();
    }

    private static string PhaseActivityName(CalendarOperationPhase phase) => phase switch
    {
        CalendarOperationPhase.Discovery => "caldav.phase.discovery",
        CalendarOperationPhase.Fetch => "caldav.phase.fetch",
        CalendarOperationPhase.Filter => "caldav.phase.filter",
        CalendarOperationPhase.Expand => "caldav.phase.expand",
        CalendarOperationPhase.Reconcile => "caldav.phase.reconcile",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    private static string PhaseName(CalendarOperationPhase phase) => phase switch
    {
        CalendarOperationPhase.Discovery => "discovery",
        CalendarOperationPhase.Fetch => "fetch",
        CalendarOperationPhase.Filter => "filter",
        CalendarOperationPhase.Expand => "expand",
        CalendarOperationPhase.Reconcile => "reconcile",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    private static string ClassifyException(Exception exception) => exception switch
    {
        TimeoutException or TaskCanceledException => "timeout",
        HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } => "response_ended",
        HttpRequestException or IOException => "connection_error",
        CalendarDiscoveryProtocolException => "protocol_error",
        _ => "internal_error"
    };

    private static string OutcomeName(CalendarOperationOutcome outcome) => outcome switch
    {
        CalendarOperationOutcome.Success => "success",
        CalendarOperationOutcome.InputRequired => "input_required",
        CalendarOperationOutcome.Cancelled => "cancelled",
        CalendarOperationOutcome.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    private static string? MoveDispatch(CalendarMoveTelemetrySnapshot? telemetry) =>
        telemetry?.Dispatch switch
    {
        CalendarMoveDispatchClassification.NotAttempted => "not_attempted",
        CalendarMoveDispatchClassification.Rejected => "rejected",
        CalendarMoveDispatchClassification.Dispatched => "dispatched",
        CalendarMoveDispatchClassification.PossiblyDispatched => "possibly_dispatched",
        _ => null
    };

    private static string? MoveCollision(CalendarMoveTelemetrySnapshot? telemetry) =>
        telemetry?.Collision switch
    {
        CalendarMoveCollisionClassification.None => "none",
        CalendarMoveCollisionClassification.SourceRevision => "source_revision",
        CalendarMoveCollisionClassification.DestinationHref => "destination_href",
        CalendarMoveCollisionClassification.Uid => "uid",
        CalendarMoveCollisionClassification.Unclassified => "unclassified",
        _ => null
    };

    private static string? MoveReconciliation(CalendarMoveTelemetrySnapshot? telemetry) =>
        telemetry?.Reconciliation switch
    {
        CalendarMoveReconciliationClassification.NotRun => "not_run",
        CalendarMoveReconciliationClassification.FaithfulDestinationSourceAbsent =>
            "faithful_destination_source_absent",
        CalendarMoveReconciliationClassification.DivergentDestinationSourceAbsent =>
            "divergent_destination_source_absent",
        CalendarMoveReconciliationClassification.ObservationUnavailable => "observation_unavailable",
        CalendarMoveReconciliationClassification.UnchangedSourceDestinationAbsent =>
            "unchanged_source_destination_absent",
        CalendarMoveReconciliationClassification.Indeterminate => "indeterminate",
        _ => null
    };
}
