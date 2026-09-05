namespace DotnetAgents.CalDav.Core.Models;

/// <summary>A bounded, actionable failure of a native Calendar protocol operation.</summary>
public sealed class CalendarProtocolException(string code, string message, bool retryable = false)
    : Exception(message)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;
}
