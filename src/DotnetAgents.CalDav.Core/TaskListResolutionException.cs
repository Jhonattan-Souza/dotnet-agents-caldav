namespace DotnetAgents.CalDav.Core;

/// <summary>
/// Thrown when a user-facing task list name cannot be resolved to a single task list.
/// Contains available list names to aid in error reporting.
/// </summary>
public sealed class TaskListResolutionException : InvalidOperationException
{
    /// <summary>The user-facing name that could not be resolved.</summary>
    public string UserInput { get; }

    /// <summary>The display names of all available task lists at the time of resolution.</summary>
    public IReadOnlyList<string> AvailableLists { get; }

    public TaskListResolutionException(string userInput, IReadOnlyList<string> availableLists, string message)
        : base(message)
    {
        UserInput = userInput;
        AvailableLists = availableLists;
    }
}
