using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>
/// Resolves a user-facing task list name to a single <see cref="TaskList"/> using deterministic rules.
/// Resolution order: (1) exact case-insensitive display name match, (2) known alias match,
/// (3) configured default list, (4) throw with candidate information.
/// </summary>
public interface ITaskListResolver
{
    /// <summary>
    /// Resolves a user-facing name to a single task list.
    /// </summary>
    /// <param name="taskLists">The available task lists from the server.</param>
    /// <param name="userInput">The user-provided name or phrase (may be null to use default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved <see cref="TaskList"/>.</returns>
    /// <exception cref="TaskListResolutionException">
    /// Thrown when the input is ambiguous or matches no available list.
    /// </exception>
    Task<TaskList> ResolveAsync(
        IReadOnlyList<TaskList> taskLists,
        string? userInput,
        CancellationToken cancellationToken = default);
}
