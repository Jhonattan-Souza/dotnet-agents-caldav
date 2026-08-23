using System.Diagnostics;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class CalendarQueryTelemetry
{
    internal const string InstrumentationName = "DotnetAgents.CalDav";
    private const string InstrumentationVersion = "0.1.0";
    private static readonly ActivitySource Source = new(InstrumentationName, InstrumentationVersion);
    private static readonly string[] StartCounters =
    [
        "caldav.query.candidate_count",
        "caldav.query.multiget_resource_count",
        "caldav.query.snapshot_count",
        "caldav.query.evaluation_count",
        "caldav.query.serialization_count",
        "caldav.query.page_admission_count"
    ];

    internal static void Begin(bool continuation)
    {
        var activity = FindOperation();
        if (activity?.IsAllDataRequested != true)
            return;
        activity.SetTag("caldav.query.mode", continuation ? "continue" : "start");
        if (continuation)
        {
            activity.SetTag("caldav.query.snapshot_lookup_count", 0L);
            activity.SetTag("caldav.query.page_admission_count", 0L);
            return;
        }
        foreach (var name in StartCounters)
            activity.SetTag(name, 0L);
    }

    internal static void Add(string name, int count = 1)
    {
        if (count <= 0 || FindOperation() is not { IsAllDataRequested: true } activity)
            return;
        var current = activity.GetTagItem(name) is long value ? value : 0L;
        activity.SetTag(name, current + count);
    }

    internal static void ObserveMultiget(int requestedCount)
    {
        var activity = FindOperation();
        if (activity?.IsAllDataRequested != true)
            return;
        activity.SetTag("caldav.query.fetch_mode", "multiget");
        Add("caldav.query.multiget_resource_count", requestedCount);
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
}
