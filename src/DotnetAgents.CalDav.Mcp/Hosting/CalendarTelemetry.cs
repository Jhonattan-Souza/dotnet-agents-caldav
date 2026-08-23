using System.Diagnostics;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Mcp.Hosting;

internal static class CalendarTelemetry
{
    internal const string InstrumentationName = "DotnetAgents.CalDav";
    internal const string InstrumentationVersion = "0.1.0";
    private static readonly ActivitySource Source = new(InstrumentationName, InstrumentationVersion);

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
        "calendars.list" or "calendar_entities.query" or "calendar_occurrences.query"
            or "todos.query" or "calendar_resources.get" or "events.create" or "events.patch"
            or "todos.create" or "todos.patch" or "todos.complete" or "calendar_occurrences.add"
            or "calendar_occurrences.exclude" or "calendar_occurrences.restore_exclusion"
            or "calendar_occurrences.cancel" or "calendar_occurrences.restore_cancellation"
            or "calendar_resources.move" or "calendar_resources.delete"
            or "calendar_resources.exact_get" or "calendar_resources.exact_create"
            or "calendar_resources.exact_replace" or "calendar_resources.exact_move" => toolName,
        _ => "unknown"
    };

    private static string EntityKindName(CalendarTelemetryEntityKind entityKind) => entityKind switch
    {
        CalendarTelemetryEntityKind.Event => "event",
        CalendarTelemetryEntityKind.Todo => "todo",
        _ => throw new ArgumentOutOfRangeException(nameof(entityKind), entityKind, null)
    };
}

internal enum CalendarTelemetryEntityKind
{
    Event,
    Todo
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

    internal void Complete(
        string outcome,
        string? errorCode = null,
        string? errorCategory = null,
        string? mutationState = null,
        string? errorPhase = null,
        bool? retryable = null,
        CalendarMoveTelemetrySnapshot? moveTelemetry = null)
    {
        if (!_operation.IsAllDataRequested)
            return;

        if (outcome is not ("success" or "input_required" or "cancelled" or "error"))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);

        _operation.SetTag("caldav.outcome", outcome);
        _operation.SetTag("caldav.error.code", errorCode);
        _operation.SetTag("caldav.error.category", errorCategory);
        _operation.SetTag("caldav.error.phase", errorPhase);
        _operation.SetTag("caldav.error.retryable", retryable);
        _operation.SetTag("caldav.mutation.state", mutationState);
        _operation.SetTag("caldav.move.dispatch", MoveDispatch(moveTelemetry?.Dispatch));
        _operation.SetTag("caldav.move.collision", MoveCollision(moveTelemetry?.Collision));
        _operation.SetTag("caldav.move.reconciliation", MoveReconciliation(moveTelemetry?.Reconciliation));
        if (string.Equals(outcome, "error", StringComparison.Ordinal))
        {
            _operation.SetTag("error.type", errorCode is null ? null : $"caldav.{errorCode}");
            _operation.SetStatus(ActivityStatusCode.Error);
        }
    }

    internal void Fail(Exception exception)
    {
        if (!_operation.IsAllDataRequested)
            return;

        _operation.SetTag("caldav.outcome", "error");
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

    private static string? MoveDispatch(CalendarMoveDispatchClassification? classification) => classification switch
    {
        CalendarMoveDispatchClassification.NotAttempted => "not_attempted",
        CalendarMoveDispatchClassification.Rejected => "rejected",
        CalendarMoveDispatchClassification.Dispatched => "dispatched",
        CalendarMoveDispatchClassification.PossiblyDispatched => "possibly_dispatched",
        _ => null
    };

    private static string? MoveCollision(CalendarMoveCollisionClassification? classification) => classification switch
    {
        CalendarMoveCollisionClassification.None => "none",
        CalendarMoveCollisionClassification.SourceRevision => "source_revision",
        CalendarMoveCollisionClassification.DestinationHref => "destination_href",
        CalendarMoveCollisionClassification.Uid => "uid",
        CalendarMoveCollisionClassification.Unclassified => "unclassified",
        _ => null
    };

    private static string? MoveReconciliation(CalendarMoveReconciliationClassification? classification) => classification switch
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
