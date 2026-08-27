using System.Diagnostics;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class CalendarQueryTelemetry
{
    internal const string InstrumentationName = "DotnetAgents.CalDav";
    private const string InstrumentationVersion = "0.1.0";
    private const string OperationStateProperty = "DotnetAgents.CalDav.QueryTelemetryState";
    private static readonly ActivitySource Source = new(InstrumentationName, InstrumentationVersion);
    private static readonly CalendarQueryCounter[] StartCounters =
    [
        CalendarQueryCounter.Candidate,
        CalendarQueryCounter.MultigetResource,
        CalendarQueryCounter.DirectGetResource,
        CalendarQueryCounter.DirectGetAttempt,
        CalendarQueryCounter.DisappearedResource,
        CalendarQueryCounter.Snapshot,
        CalendarQueryCounter.Parse,
        CalendarQueryCounter.Evaluation,
        CalendarQueryCounter.Serialization,
        CalendarQueryCounter.PageAdmission
    ];

    internal static void Begin(CalendarQueryMode mode)
    {
        var activity = FindOperation();
        if (activity?.IsAllDataRequested != true)
            return;
        var state = new OperationTelemetryState(activity);
        activity.SetCustomProperty(OperationStateProperty, state);
        state.Begin(mode);
    }

    internal static void Add(CalendarQueryCounter counter, int count = 1)
    {
        if (count <= 0 || FindState() is not { } state)
            return;
        state.Add(counter, count);
    }

    internal static void ObserveMultigetAttempt(int requestedCount) =>
        Add(CalendarQueryCounter.MultigetResource, requestedCount);

    internal static void ObserveMultigetSuccess() => FindState()?.ObserveMultigetSuccess();

    internal static void ObserveDirectGetFallback() => FindState()?.ObserveDirectGetFallback();

    internal static Activity? StartPhase(CalendarQueryPhase phase)
    {
        var phaseName = PhaseName(phase);
        var operation = FindOperation();
        if (operation is null)
            return null;
        var activity = Source.StartActivity($"caldav.query.phase.{phaseName}", ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true)
            activity.SetTag("caldav.query.phase", phaseName);
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

    private static string ModeName(CalendarQueryMode mode) => mode switch
    {
        CalendarQueryMode.Start => "start",
        CalendarQueryMode.Continue => "continue",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    private static string CounterName(CalendarQueryCounter counter) => counter switch
    {
        CalendarQueryCounter.Candidate => "caldav.query.candidate_count",
        CalendarQueryCounter.MultigetResource => "caldav.query.multiget_resource_count",
        CalendarQueryCounter.DirectGetResource => "caldav.query.direct_get_resource_count",
        CalendarQueryCounter.DirectGetAttempt => "caldav.query.direct_get_attempt_count",
        CalendarQueryCounter.DisappearedResource => "caldav.query.disappeared_resource_count",
        CalendarQueryCounter.Snapshot => "caldav.query.snapshot_count",
        CalendarQueryCounter.Parse => "caldav.query.parse_count",
        CalendarQueryCounter.Evaluation => "caldav.query.evaluation_count",
        CalendarQueryCounter.Serialization => "caldav.query.serialization_count",
        CalendarQueryCounter.SnapshotLookup => "caldav.query.snapshot_lookup_count",
        CalendarQueryCounter.PageAdmission => "caldav.query.page_admission_count",
        _ => throw new ArgumentOutOfRangeException(nameof(counter), counter, null)
    };

    private static string PhaseName(CalendarQueryPhase phase) => phase switch
    {
        CalendarQueryPhase.Discovery => "discovery",
        CalendarQueryPhase.Candidate => "candidate",
        CalendarQueryPhase.Fetch => "fetch",
        CalendarQueryPhase.Evaluation => "evaluation",
        CalendarQueryPhase.Serialization => "serialization",
        CalendarQueryPhase.Reservation => "reservation",
        CalendarQueryPhase.SnapshotLookup => "snapshot_lookup",
        CalendarQueryPhase.PageAdmission => "page_admission",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    private static string FetchModeName(CalendarQueryFetchMode mode) => mode switch
    {
        CalendarQueryFetchMode.Multiget => "multiget",
        CalendarQueryFetchMode.DirectGetFallback => "direct_get_fallback",
        CalendarQueryFetchMode.Mixed => "mixed",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    private sealed class OperationTelemetryState(Activity activity)
    {
        private readonly object _gate = new();
        private readonly Dictionary<CalendarQueryCounter, long> _counters = [];
        private CalendarQueryFetchMode? _fetchMode;

        internal void Begin(CalendarQueryMode mode)
        {
            lock (_gate)
            {
                activity.SetTag("caldav.query.mode", ModeName(mode));
                if (mode == CalendarQueryMode.Continue)
                {
                    SetCounter(CalendarQueryCounter.SnapshotLookup, 0);
                    SetCounter(CalendarQueryCounter.PageAdmission, 0);
                    return;
                }
                if (mode != CalendarQueryMode.Start)
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
                foreach (var counter in StartCounters)
                    SetCounter(counter, 0);
            }
        }

        internal void Add(CalendarQueryCounter counter, int count)
        {
            lock (_gate)
            {
                var updated = _counters.GetValueOrDefault(counter) + count;
                SetCounter(counter, updated);
            }
        }

        internal void ObserveMultigetSuccess()
        {
            lock (_gate)
            {
                _fetchMode = _fetchMode is CalendarQueryFetchMode.DirectGetFallback
                    or CalendarQueryFetchMode.Mixed
                    ? CalendarQueryFetchMode.Mixed
                    : CalendarQueryFetchMode.Multiget;
                activity.SetTag("caldav.query.fetch_mode", FetchModeName(_fetchMode.Value));
            }
        }

        internal void ObserveDirectGetFallback()
        {
            lock (_gate)
            {
                _fetchMode = _fetchMode is CalendarQueryFetchMode.Multiget
                    or CalendarQueryFetchMode.Mixed
                    ? CalendarQueryFetchMode.Mixed
                    : CalendarQueryFetchMode.DirectGetFallback;
                activity.SetTag("caldav.query.fetch_mode", FetchModeName(_fetchMode.Value));
                activity.SetTag("caldav.query.fallback_reason", "multiget_unavailable");
            }
        }

        private void SetCounter(CalendarQueryCounter counter, long value)
        {
            _counters[counter] = value;
            activity.SetTag(CounterName(counter), value);
        }
    }
}

internal enum CalendarQueryMode
{
    Start,
    Continue
}

internal enum CalendarQueryCounter
{
    Candidate,
    MultigetResource,
    DirectGetResource,
    DirectGetAttempt,
    DisappearedResource,
    Snapshot,
    Parse,
    Evaluation,
    Serialization,
    SnapshotLookup,
    PageAdmission
}

internal enum CalendarQueryPhase
{
    Discovery,
    Candidate,
    Fetch,
    Evaluation,
    Serialization,
    Reservation,
    SnapshotLookup,
    PageAdmission
}

internal enum CalendarQueryFetchMode
{
    Multiget,
    DirectGetFallback,
    Mixed
}
