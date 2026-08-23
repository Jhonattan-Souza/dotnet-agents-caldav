using System.Diagnostics;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class CalendarHttpTelemetry
{
    internal const string InstrumentationName = "DotnetAgents.CalDav.Http";

    internal static readonly ActivitySource ActivitySource = new(InstrumentationName);

    internal static readonly HttpRequestOptionsKey<AttemptSequence> AttemptSequenceKey =
        new("DotnetAgents.CalDav.Telemetry.AttemptSequence");

    internal sealed class AttemptSequence
    {
        private int _attempt = -1;

        internal int NextResendCount() => Interlocked.Increment(ref _attempt);
    }
}
