using System.Diagnostics;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class CalendarHttpTelemetry
{
    private static readonly AsyncLocal<int> AbsenceProbeDepth = new();
    private static readonly AsyncLocal<CalendarDirectGetReadMeter?> QueryResourceReadMeter = new();

    internal const string InstrumentationName = "DotnetAgents.CalDav.Http";

    internal static readonly ActivitySource ActivitySource = new(InstrumentationName);

    internal static readonly HttpRequestOptionsKey<CalendarHttpRequestPurpose> RequestPurposeKey =
        new("DotnetAgents.CalDav.Telemetry.RequestPurpose");

    internal static readonly HttpRequestOptionsKey<AttemptSequence> AttemptSequenceKey =
        new("DotnetAgents.CalDav.Telemetry.AttemptSequence");

    internal static readonly HttpRequestOptionsKey<int> ResendCountKey =
        new("DotnetAgents.CalDav.Telemetry.ResendCount");

    internal static readonly HttpRequestOptionsKey<CalendarDirectGetReadMeter> DirectGetMeterKey =
        new("DotnetAgents.CalDav.Telemetry.DirectGetMeter");

    internal static readonly HttpRequestOptionsKey<int> MultigetResourceCountKey =
        new("DotnetAgents.CalDav.Telemetry.MultigetResourceCount");

    internal static bool IsAbsenceProbe => AbsenceProbeDepth.Value > 0;
    internal static bool IsQueryResourceRead => QueryResourceReadMeter.Value is not null;

    internal static IDisposable BeginAbsenceProbe()
    {
        var previousDepth = AbsenceProbeDepth.Value;
        AbsenceProbeDepth.Value = previousDepth + 1;
        return new AbsenceProbeScope(previousDepth);
    }

    internal static IDisposable BeginQueryResourceRead(CalendarDirectGetReadMeter meter)
    {
        var previous = QueryResourceReadMeter.Value;
        QueryResourceReadMeter.Value = meter;
        return new QueryResourceReadScope(previous);
    }

    internal static void MarkAbsenceProbe(HttpRequestMessage request) =>
        request.Options.Set(RequestPurposeKey, CalendarHttpRequestPurpose.AbsenceProbe);

    internal static void MarkQueryResourceRead(HttpRequestMessage request)
    {
        request.Options.Set(RequestPurposeKey, CalendarHttpRequestPurpose.QueryResourceRead);
        request.Options.Set(DirectGetMeterKey, QueryResourceReadMeter.Value!);
    }

    internal static void MarkQueryMultiget(HttpRequestMessage request, int resourceCount) =>
        request.Options.Set(MultigetResourceCountKey, resourceCount);

    private sealed class AbsenceProbeScope(int previousDepth) : IDisposable
    {
        public void Dispose() => AbsenceProbeDepth.Value = previousDepth;
    }

    private sealed class QueryResourceReadScope(CalendarDirectGetReadMeter? previous) : IDisposable
    {
        public void Dispose() => QueryResourceReadMeter.Value = previous;
    }

    internal sealed class AttemptSequence
    {
        private int _attempt = -1;

        internal int NextResendCount() => Interlocked.Increment(ref _attempt);
    }
}

internal enum CalendarHttpRequestPurpose
{
    AbsenceProbe = 1,
    QueryResourceRead
}

internal enum CalendarHttpObservation
{
    ExpectedAbsence,
    ResourceDisappeared
}
