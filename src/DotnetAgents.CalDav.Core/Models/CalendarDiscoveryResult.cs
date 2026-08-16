namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Scoped Calendar discovery result returned by the Calendar service.</summary>
public sealed record CalendarDiscoveryResult(
    IReadOnlyList<CalendarDescriptor> Items,
    IReadOnlyList<CalendarDiagnostic> Diagnostics);

/// <summary>Bounded, non-sensitive discovery diagnostic.</summary>
public sealed record CalendarDiagnostic(string Code, string Message, CalendarDiagnosticSeverity Severity);

/// <summary>Severity of a Calendar discovery diagnostic.</summary>
public enum CalendarDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>Calendar Entity Kind used for independent default selection.</summary>
public enum CalendarEntityKind
{
    Event,
    Todo
}

/// <summary>Signals that Calendar discovery exceeded the bounded admission limit.</summary>
public sealed class CalendarDiscoveryLimitException(int calendarCount) : Exception
{
    public int CalendarCount { get; } = calendarCount;
}

/// <summary>Signals a CalDAV discovery response that cannot be used safely.</summary>
public sealed class CalendarDiscoveryProtocolException(string message) : Exception(message);

/// <summary>Signals that a mandatory CalDAV REPORT filter is unsupported.</summary>
public sealed class CalendarDiscoveryUnsupportedCapabilityException(string message) : Exception(message);

/// <summary>Typed outcome of resolving a configured default Calendar.</summary>
public sealed record CalendarSelectionResult(
    CalendarSelectionCode Code,
    CalendarDescriptor? Calendar,
    IReadOnlyList<CalendarDescriptor> Candidates)
{
    public static CalendarSelectionResult Success(CalendarDescriptor calendar) => new(CalendarSelectionCode.Success, calendar, []);

    public static CalendarSelectionResult Failure(CalendarSelectionCode code, IReadOnlyList<CalendarDescriptor>? candidates = null) =>
        new(code, null, candidates ?? []);
}

/// <summary>Closed selection result codes available to Calendar default resolution.</summary>
public enum CalendarSelectionCode
{
    Success,
    NotFound,
    Ambiguous,
    OutsideScope,
    UnsupportedCapability
}
