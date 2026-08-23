using System.Diagnostics;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class CalendarQueryTelemetry
{
    internal const string InstrumentationName = "DotnetAgents.CalDav";
    private const string InstrumentationVersion = "0.1.0";
    private static readonly ActivitySource Source = new(InstrumentationName, InstrumentationVersion);
    private const string OperationStateProperty = "DotnetAgents.CalDav.QueryTelemetryState";
    private static readonly string[] StartCounters =
    [
        "caldav.query.candidate_count",
        "caldav.query.multiget_resource_count",
        "caldav.query.direct_get_resource_count",
        "caldav.query.direct_get_attempt_count",
        "caldav.query.disappeared_resource_count",
        "caldav.query.snapshot_count",
        "caldav.query.parse_count",
        "caldav.query.evaluation_count",
        "caldav.query.serialization_count",
        "caldav.query.page_admission_count"
    ];

    internal static void Begin(bool continuation)
    {
        var activity = FindOperation();
        if (activity?.IsAllDataRequested != true)
            return;
        var state = new OperationTelemetryState(activity);
        activity.SetCustomProperty(OperationStateProperty, state);
        state.Set("caldav.query.mode", continuation ? "continue" : "start");
        if (continuation)
        {
            state.Set("caldav.query.snapshot_lookup_count", 0L);
            state.Set("caldav.query.page_admission_count", 0L);
            return;
        }
        foreach (var name in StartCounters)
            state.Set(name, 0L);
    }

    internal static void Add(string name, int count = 1)
    {
        if (count <= 0 || FindState() is not { } state)
            return;
        state.Add(name, count);
    }

    internal static void ObserveMultigetAttempt(int requestedCount) =>
        Add("caldav.query.multiget_resource_count", requestedCount);

    internal static void ObserveMultigetSuccess()
    {
        FindState()?.ObserveMultigetSuccess();
    }

    internal static void ObserveDirectGetFallback()
    {
        FindState()?.ObserveDirectGetFallback();
    }

    internal static Activity? StartPhase(string phase)
    {
        if (phase is not ("discovery" or "candidate" or "fetch" or "evaluation" or "serialization"
            or "reservation" or "snapshot_lookup" or "page_admission"))
            throw new ArgumentOutOfRangeException(nameof(phase), phase, null);
        var operation = FindOperation();
        if (operation is null)
            return null;
        var activity = Source.StartActivity($"caldav.query.phase.{phase}", ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true)
            activity.SetTag("caldav.query.phase", phase);
        return activity;
    }

    private static Activity? FindOperation()
    {
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
        {
            if (activity.Source.Name == InstrumentationName && activity.OperationName == "caldav.operation")
                return activity;
        }
        return null;
    }

    private static OperationTelemetryState? FindState()
    {
        var operation = FindOperation();
        return operation?.IsAllDataRequested == true
            ? operation.GetCustomProperty(OperationStateProperty) as OperationTelemetryState
            : null;
    }

    private sealed class OperationTelemetryState(Activity activity)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
        private string? _fetchMode;

        internal void Set(string name, object value)
        {
            lock (_gate)
                activity.SetTag(name, value);
        }

        internal void Add(string name, int count)
        {
            lock (_gate)
            {
                var updated = _counters.GetValueOrDefault(name) + count;
                _counters[name] = updated;
                activity.SetTag(name, updated);
            }
        }

        internal void ObserveMultigetSuccess()
        {
            lock (_gate)
            {
                _fetchMode = _fetchMode is "direct_get_fallback" or "mixed" ? "mixed" : "multiget";
                activity.SetTag("caldav.query.fetch_mode", _fetchMode);
            }
        }

        internal void ObserveDirectGetFallback()
        {
            lock (_gate)
            {
                _fetchMode = _fetchMode is "multiget" or "mixed" ? "mixed" : "direct_get_fallback";
                activity.SetTag("caldav.query.fetch_mode", _fetchMode);
                activity.SetTag("caldav.query.fallback_reason", "multiget_unavailable");
            }
        }
    }
}
