namespace DotnetAgents.CalDav.Core.Models;

/// <summary>A bounded, actionable failure of a native Calendar protocol operation.</summary>
public sealed class CalendarProtocolException(string code, string message, bool retryable = false)
    : Exception(message)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;

    /// <summary>Per-property rejection evidence from an atomic property write, when the server reported it.</summary>
    public IReadOnlyList<CalendarPropertyRejection> RejectedProperties { get; init; } = [];
}

/// <summary>
/// One requested collection property the server did not apply, named by its public tool member.
/// Status 424 means the property was valid but not applied because another property failed.
/// </summary>
public sealed record CalendarPropertyRejection(string Property, int StatusCode);
