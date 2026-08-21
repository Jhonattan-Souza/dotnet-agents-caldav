using System.Diagnostics;
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
        string? mutationState = null)
    {
        if (!_operation.IsAllDataRequested)
            return;

        _operation.SetTag("caldav.outcome", outcome);
        _operation.SetTag("caldav.error.code", errorCode);
        _operation.SetTag("caldav.error.category", errorCategory);
        _operation.SetTag("caldav.mutation.state", mutationState);
        if (string.Equals(outcome, "error", StringComparison.Ordinal))
            _operation.SetStatus(ActivityStatusCode.Error);
    }

    internal void Fail(Exception exception)
    {
        if (!_operation.IsAllDataRequested)
            return;

        _operation.SetTag("caldav.outcome", "error");
        _operation.SetTag("error.type", exception.GetType().FullName);
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
}
