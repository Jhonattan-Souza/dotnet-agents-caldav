using System.Diagnostics;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class CalendarHttpTelemetry
{
    private static readonly AsyncLocal<int> AbsenceProbeDepth = new();

    internal const string AbsenceProbe = "absence_probe";
    internal const string InstrumentationName = "DotnetAgents.CalDav.Http";

    internal static readonly ActivitySource ActivitySource = new(InstrumentationName);

    internal static readonly HttpRequestOptionsKey<string> RequestPurposeKey =
        new("DotnetAgents.CalDav.Telemetry.RequestPurpose");

    internal static readonly HttpRequestOptionsKey<AttemptSequence> AttemptSequenceKey =
        new("DotnetAgents.CalDav.Telemetry.AttemptSequence");

    internal static readonly HttpRequestOptionsKey<int> ResendCountKey =
        new("DotnetAgents.CalDav.Telemetry.ResendCount");

    internal static bool IsAbsenceProbe => AbsenceProbeDepth.Value > 0;

    internal static IDisposable BeginAbsenceProbe()
    {
        var previousDepth = AbsenceProbeDepth.Value;
        AbsenceProbeDepth.Value = previousDepth + 1;
        return new AbsenceProbeScope(previousDepth);
    }

    internal static void MarkAbsenceProbe(HttpRequestMessage request) =>
        request.Options.Set(RequestPurposeKey, AbsenceProbe);

    private sealed class AbsenceProbeScope(int previousDepth) : IDisposable
    {
        public void Dispose() => AbsenceProbeDepth.Value = previousDepth;
    }

    internal sealed class AttemptSequence
    {
        private int _attempt = -1;

        internal int NextResendCount() => Interlocked.Increment(ref _attempt);
    }
}
